using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.OdaOrg.Models;

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
            using var handler = new HttpClientHandler { UseCookies = true, AllowAutoRedirect = true, CookieContainer = new() };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SchulyOdaOrgPlugin");

            // 1. Login.
            var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["cusername"] = username,
                ["cpassword"] = password,
            });
            await http.PostAsync($"{baseUrl}/modules.php?name=IVVerwaltung&a=11140", loginForm, ct);

            // 2. Home page — confirms auth + lists the dynamic boxes.
            var home = await http.GetStringAsync($"{baseUrl}/modules.php?name=IVVerwaltung&a=99900", ct);
            if (!home.Contains("Abmelden", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("OdaOrg login failed for {User} — no authenticated home page", username);
                return null;
            }

            var homeDoc = Parser.ParseDocument(home);
            var result = new OdaScrape();
            var gradeLinks = new HashSet<string>();

            // 3. Fetch every AJAX box fragment.
            foreach (var box in homeDoc.QuerySelectorAll("[data-source][data-module]"))
            {
                var source = box.GetAttribute("data-source");
                var module = box.GetAttribute("data-module");
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(module)) continue;

                string fragment;
                try
                {
                    fragment = await http.GetStringAsync(
                        $"{baseUrl}/modules.php?name={module}&a={source}&caction=00&api=box", ct);
                }
                catch (Exception ex) { logger.LogDebug(ex, "box {Module}/{Source} fetch failed", module, source); continue; }

                var doc = Parser.ParseDocument(fragment);
                result.Profile ??= ParseProfile(doc);
                result.CourseDays.AddRange(ParseCourseDays(doc));
                foreach (var a in doc.QuerySelectorAll("a[href*='nlbvbewertungid']"))
                {
                    var href = a.GetAttribute("href");
                    if (!string.IsNullOrWhiteSpace(href)) gradeLinks.Add(href);
                }
            }

            // 4. Each grade-evaluation page renders server-side: module + Schlussnote.
            foreach (var href in gradeLinks)
            {
                var url = href.StartsWith("http") ? href : $"{baseUrl}/{href.TrimStart('/')}";
                try
                {
                    var page = await http.GetStringAsync(url, ct);
                    var grade = ParseGrade(page);
                    if (grade is not null) result.Grades.Add(grade);
                }
                catch (Exception ex) { logger.LogDebug(ex, "grade page {Url} failed", url); }
            }

            logger.LogInformation("OdaOrg scrape: profile={HasProfile} grades={Grades} courseDays={Days}",
                result.Profile is not null, result.Grades.Count, result.CourseDays.Count);
            return result;
        }

        private static OdaProfile? ParseProfile(IDocument doc)
        {
            var dts = doc.QuerySelectorAll("dt");
            if (dts.Length == 0) return null;

            string? Field(string label) => dts
                .FirstOrDefault(d => d.TextContent.Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
                ?.NextElementSibling?.TextContent.Trim();

            var first = Field("Vorname");
            var last = Field("Nachname");
            if (first is null && last is null) return null;

            return new OdaProfile
            {
                FirstName = first,
                LastName = last,
                Gender = Field("Geschlecht"),
                Birthday = ParseDate(Field("Geburtsdatum")),
                Email = Field("E-Mail") ?? Field("E-Mail Adresse") ?? Field("Email"),
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

        private static DateTime WithTime(DateOnly date, string? time)
        {
            var dt = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var m = time is null ? Match.Empty : TimeRx.Match(time);
            return m.Success ? dt.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value)) : dt;
        }

        private static decimal? ParseDecimal(string? s) =>
            decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

        private static string CollapseWs(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
    }
}
