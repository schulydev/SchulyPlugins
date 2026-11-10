using Microsoft.Extensions.Configuration;
using Microsoft.Kiota.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>Where the official card can be read from: the mobile REST report, and the web page as a fallback.</summary>
    public interface IStudentIdCardSource
    {
        /// <summary>The card HTML from the mobile <c>cockpitReport</c>. Throws <see cref="ApiException"/> with the HTTP status on a refusal.</summary>
        Task<string?> GetMobileAsync(int reportId, CancellationToken ct);

        /// <summary>The card HTML from the Schulnetz web page, or null when this caller has no way to open the web page.</summary>
        Task<string?> GetWebAsync(CancellationToken ct);
    }

    /// <summary>
    /// Fetches the Schulnetz-rendered Lernendenausweis: the mobile report first, the web page when the school does not
    /// offer it there. The card is sanitized and handed straight back; it is never stored, cached or logged here.
    /// </summary>
    public class StudentIdCardService(IConfiguration configuration, TimeProvider? time = null)
    {
        public const int DefaultReportId = 50505;
        public const string MobileSource = "mobile";
        public const string WebSource = "web";

        /// <summary>Upper bound for one card; a real one with photo, QR and logo is a few hundred KB.</summary>
        private const int MaxHtmlLength = 8 * 1024 * 1024;

        public int ReportId => int.TryParse(configuration["StudentIdCard:ReportId"], out var id) && id > 0 ? id : DefaultReportId;

        public async Task<StudentIdCardResponse> FetchAsync(IStudentIdCardSource source, bool mobileAvailable, CancellationToken ct)
        {
            try
            {
                if (mobileAvailable)
                {
                    try
                    {
                        if (Usable(await source.GetMobileAsync(ReportId, ct)) is { } mobile) return Card(mobile, MobileSource);
                    }
                    catch (ApiException ex) when (ex.ResponseStatusCode is 403 or 404)
                    {
                        // The school does not offer the mobile report; the web page may still have the card.
                    }
                }

                if (Usable(await source.GetWebAsync(ct)) is { } web) return Card(web, WebSource);
                throw StudentIdCardException.NotOffered();
            }
            catch (ApiException ex) when (ex.ResponseStatusCode == 401)
            {
                throw StudentIdCardException.SignInExpired();
            }
            catch (ApiException)
            {
                throw StudentIdCardException.Unavailable();
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw StudentIdCardException.Unavailable();
            }
        }

        private StudentIdCardResponse Card(string html, string source) => new(html, (time ?? TimeProvider.System).GetUtcNow(), source);

        private static string? Usable(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (raw.Length > MaxHtmlLength) throw StudentIdCardException.Unavailable();
            var html = StudentIdCardHtml.Sanitize(raw);
            return string.IsNullOrWhiteSpace(html) ? null : html;
        }
    }

    /// <summary>The card for a stored account: mobile with the account's token, web through its Schulnetz web session (opened again once when it died).</summary>
    public sealed class AccountStudentIdCardSource(SchulwareApiClient client, SchulwareAccount account, TokenRefreshService refresh) : IStudentIdCardSource
    {
        /// <summary>True once a new web session was opened, so the caller saves the account's secrets.</summary>
        public bool SessionChanged { get; private set; }

        public async Task<string?> GetMobileAsync(int reportId, CancellationToken ct) =>
            (await client.Api.Mobile.Studentidcard[reportId].GetAsync(cancellationToken: ct))?.Html;

        public async Task<string?> GetWebAsync(CancellationToken ct)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (string.IsNullOrEmpty(account.WebSessionId))
                {
                    if (!await refresh.RefreshViaRunnerAsync(account, ct) || string.IsNullOrEmpty(account.WebSessionId))
                        throw StudentIdCardException.SignInExpired();
                    SessionChanged = true;
                }

                var result = await client.Api.Websession.Scrape.PostAsync(new WebScrapeRequestDto
                {
                    Page = "student_id",
                    SessionId = account.WebSessionId,
                    Id = account.WebSessionUserId,
                    Transid = account.WebSessionTransId,
                    UserAgent = account.UserAgent,
                }, cancellationToken: ct);
                if (result?.Success == true) return result.StudentId?.Html;

                account.WebSessionId = null;
                SessionChanged = true;
            }
            throw StudentIdCardException.SignInExpired();
        }
    }

    /// <summary>The card for private mode: only the caller's own mobile token is known, so there is no web fallback.</summary>
    public sealed class StatelessStudentIdCardSource(SchulwareApiClient client) : IStudentIdCardSource
    {
        public async Task<string?> GetMobileAsync(int reportId, CancellationToken ct) =>
            (await client.Api.Mobile.Studentidcard[reportId].GetAsync(cancellationToken: ct))?.Html;

        public Task<string?> GetWebAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
