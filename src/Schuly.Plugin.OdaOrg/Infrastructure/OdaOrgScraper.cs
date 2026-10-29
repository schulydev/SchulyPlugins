using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.OdaOrg.Models;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.OdaOrg.Infrastructure
{
    /// <summary>
    /// Logs into an OdaOrg portal (plain form POST → PHPSESSID) and scrapes the
    /// AJAX "box" fragments + per-module grade pages with AngleSharp. No JS engine
    /// needed: every fragment renders server-side once the right query params
    /// (<c>caction=00&amp;api=box</c>) are supplied.
    /// </summary>
    public class OdaOrgScraper(ILogger<OdaOrgScraper> logger)
    {
        private static readonly HtmlParser Parser = new();
        private static readonly Regex DateRx = new(@"(\d{2})\.(\d{2})\.(\d{4})", RegexOptions.Compiled);
        private static readonly Regex TimeRx = new(@"(\d{1,2}):(\d{2})", RegexOptions.Compiled);
        private static readonly Regex ModuleRx = new(@"Modul\s*\d+\s*-\s*[^\n:]+", RegexOptions.Compiled);
        private static readonly Regex RoundedRx = new(@"(?<!un)gerundet\)\s*:?\s*([\d.]+)", RegexOptions.Compiled);
        private static readonly Regex UnroundedRx = new(@"ungerundet\)\s*:?\s*([\d.]+)", RegexOptions.Compiled);

        public async Task<OdaScrape?> ScrapeAsync(string baseUrl, string username, string password, CancellationToken ct)
        {
            baseUrl = baseUrl.TrimEnd('/');
            if (!BaseUrlGuard.IsAllowed(baseUrl)) throw new HttpRequestException("Target is not allowed.");
            var baseHost = new Uri(baseUrl).IdnHost;
            using var http = GuardedHttp.Create(new CookieContainer(), timeoutSeconds: 20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SchulyOdaOrgPlugin");

            var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["cusername"] = username,
                ["cpassword"] = password,
            });
            using (await SendAsync(http, HttpMethod.Post, $"{baseUrl}/modules.php?name=IVVerwaltung&a=11140", loginForm, ct)) { }

            var home = await GetStringAsync(http, $"{baseUrl}/modules.php?name=IVVerwaltung&a=99900", ct);
            if (!home.Contains("Abmelden", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("OdaOrg login failed — no authenticated home page");
                return null;
            }

            var homeDoc = Parser.ParseDocument(home);
            var result = new OdaScrape();
            var gradeLinks = new HashSet<string>();

            // Profile fields are spread across several IVAdresse boxes (person,
            // contact, address, account) — accumulate every dt/dd pair and the
            // photo across all of them, then build one profile after the sweep.
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? photo = null;

            foreach (var box in homeDoc.QuerySelectorAll("[data-source][data-module]"))
            {
                var source = box.GetAttribute("data-source");
                var module = box.GetAttribute("data-module");
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(module)) continue;

                string fragment;
                try
                {
                    fragment = await GetStringAsync(
                        http, $"{baseUrl}/modules.php?name={Uri.EscapeDataString(module)}&a={Uri.EscapeDataString(source)}&caction=00&api=box", ct);
                }
                catch (Exception ex) { logger.LogDebug(ex, "box {Module}/{Source} fetch failed", module, source); continue; }

                var doc = Parser.ParseDocument(fragment);
                CollectFields(doc, fields);
                photo ??= doc.QuerySelector("img[src^='data:image']")?.GetAttribute("src");
                result.CourseDays.AddRange(ParseCourseDays(doc));
                foreach (var a in doc.QuerySelectorAll("a[href*='nlbvbewertungid']"))
                {
                    var href = a.GetAttribute("href");
                    if (!string.IsNullOrWhiteSpace(href)) gradeLinks.Add(href);
                }
            }

            result.Profile = BuildProfile(fields, photo);

            // Each grade-evaluation page renders server-side: module + Schlussnote.
            foreach (var href in gradeLinks)
            {
                var url = href.StartsWith("http") ? href : $"{baseUrl}/{href.TrimStart('/')}";
                // Links come from fetched HTML: only follow them on the validated portal host.
                if (!Uri.TryCreate(url, UriKind.Absolute, out var linkUri)
                    || !linkUri.IdnHost.Equals(baseHost, StringComparison.OrdinalIgnoreCase)
                    || !BaseUrlGuard.IsAllowed(url))
                {
                    logger.LogDebug("Dropping grade link outside the portal host");
                    continue;
                }
                try
                {
                    var page = await GetStringAsync(http, url, ct);
                    var grade = ParseGrade(page);
                    if (grade is not null) result.Grades.Add(grade);
                }
                catch (Exception ex) { logger.LogDebug(ex, "grade page {Url} failed", url); }
            }

            logger.LogInformation("OdaOrg scrape: profile={HasProfile} grades={Grades} courseDays={Days}",
                result.Profile is not null, result.Grades.Count, result.CourseDays.Count);
            return result;
        }

        private const int MaxRedirects = 5;

        private static async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct)
        {
            using var response = await SendAsync(http, HttpMethod.Get, url, null, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }

        /// <summary>Sends a request and follows redirects manually (max 5); every Location must pass <see cref="BaseUrlGuard.IsAllowed"/> and goes through the same guarded client.</summary>
        private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string url, HttpContent? content, CancellationToken ct)
        {
            var current = new Uri(url);
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(method, current) { Content = content };
                var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
                var status = (int)response.StatusCode;
                if (status is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
                    return response;

                response.Dispose();
                if (hop >= MaxRedirects) throw new HttpRequestException("Too many redirects.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!BaseUrlGuard.IsAllowed(next.AbsoluteUri)) throw new HttpRequestException("Redirect target is not allowed.");
                if (status is 301 or 302 or 303) { method = HttpMethod.Get; content = null; }
                current = next;
            }
        }

        // Address labels repeat across address-type boxes (Privatadresse,
        // Geschäftsadresse, …) — only trust them from the private-address box,
        // otherwise we'd grab the school/business address.
        private static readonly HashSet<string> AddressLabels =
            new(StringComparer.OrdinalIgnoreCase) { "Strasse", "PLZ", "Ort", "Zusatz", "Kanton", "Land" };

        private static void CollectFields(IDocument doc, Dictionary<string, string> fields)
        {
            var title = doc.QuerySelector(".bx-title span")?.TextContent ?? "";
            var isPrivateAddress = title.Contains("Privatadresse", StringComparison.OrdinalIgnoreCase);

            foreach (var dt in doc.QuerySelectorAll("dt"))
            {
                var key = dt.TextContent.Trim();
                var val = dt.NextElementSibling?.TextContent.Trim();
                if (key.Length == 0 || string.IsNullOrWhiteSpace(val)) continue;
                if (AddressLabels.Contains(key) && !isPrivateAddress) continue;
                if (!fields.ContainsKey(key)) fields[key] = val!;
            }
        }

        private static OdaProfile? BuildProfile(Dictionary<string, string> f, string? photo)
        {
            string? Get(params string[] labels) =>
                labels.Select(l => f.TryGetValue(l, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var first = Get("Vorname");
            var last = Get("Nachname");
            if (first is null && last is null) return null;

            return new OdaProfile
            {
                FirstName = first,
                LastName = last,
                Gender = Get("Geschlecht"),
                Birthday = ParseDate(Get("Geburtsdatum")),
                Email = Get("E-Mail", "E-Mail Adresse", "Email"),
                PrivateEmail = Get("E-Mail Privat"),
                PhoneNumber = Get("Telefonnummer Mobil", "Telefonnummer Privat", "Telefonnummer Geschäft"),
                Street = Get("Strasse"),
                Zip = Get("PLZ"),
                City = Get("Ort"),
                ProfilePictureUrl = string.IsNullOrWhiteSpace(photo) ? null : photo,
            };
        }

        private static IEnumerable<CourseDay> ParseCourseDays(IDocument doc)
        {
            foreach (var table in doc.QuerySelectorAll("table"))
            {
                var rows = table.QuerySelectorAll("tr").ToList();
                if (rows.Count < 2) continue;

                var headers = rows[0].QuerySelectorAll("th,td")
                    .Select(c => c.TextContent.Trim().ToLowerInvariant().Replace("\n", " ").Replace("  ", " ")).ToList();
                int Col(params string[] names) => headers.FindIndex(h => names.Any(n => h.Contains(n)));
                int iDate = Col("datum");
                if (iDate < 0) continue; // not a course table

                int iCourse = Col("kurs"), iTopic = Col("kursthema"), iRoom = Col("raum"),
                    iFrom = Col("von"), iTo = Col("bis"), iLeiter = Col("kursleiter");

                foreach (var row in rows.Skip(1))
                {
                    var cells = row.QuerySelectorAll("td").Select(c => c.TextContent.Trim().Replace(" ", " ")).ToList();
                    if (cells.Count == 0) continue;
                    string Cell(int i) => i >= 0 && i < cells.Count ? cells[i] : "";

                    var date = ParseDate(Cell(iDate));
                    if (date is null) continue; // "Keine Daten zur Anzeige" etc.

                    var course = Cell(iCourse);
                    if (string.IsNullOrWhiteSpace(course)) continue;

                    var start = WithTime(date.Value, Cell(iFrom));
                    yield return new CourseDay
                    {
                        Course = CollapseWs(course),
                        Topic = CollapseWs(Cell(iTopic)),
                        Room = CollapseWs(Cell(iRoom)) is { Length: > 0 } r ? r : null,
                        Date = start,
                        EndDate = Cell(iTo).Length > 0 ? WithTime(date.Value, Cell(iTo)) : null,
                        Instructor = CollapseWs(Cell(iLeiter)) is { Length: > 0 } l ? l : null,
                    };
                }
            }
        }

        private static ModuleGrade? ParseGrade(string html)
        {
            var doc = Parser.ParseDocument(html);
            var text = doc.Body?.TextContent ?? "";
            var rounded = RoundedRx.Match(text);
            if (!rounded.Success) return null;

            var name = ModuleRx.Match(text);
            return new ModuleGrade
            {
                ModuleName = name.Success ? CollapseWs(name.Value) : "Modul",
                FinalGrade = ParseDecimal(rounded.Groups[1].Value) ?? 0,
                UnroundedGrade = ParseDecimal(UnroundedRx.Match(text) is { Success: true } u ? u.Groups[1].Value : null),
            };
        }

        private static DateOnly? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = DateRx.Match(s);
            return m.Success && DateOnly.TryParseExact(
                $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}", "dd.MM.yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }

        private static DateTimeOffset WithTime(DateOnly date, string? time)
        {
            var m = time is null ? Match.Empty : TimeRx.Match(time);
            if (!m.Success) return SwissTime.UtcDate(date);
            var wallClock = date.ToDateTime(TimeOnly.MinValue).AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value));
            return SwissTime.TryToUtc(wallClock, out var utc) ? utc : SwissTime.UtcDate(date);
        }

        private static decimal? ParseDecimal(string? s) =>
            decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

        private static string CollapseWs(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
    }
}
