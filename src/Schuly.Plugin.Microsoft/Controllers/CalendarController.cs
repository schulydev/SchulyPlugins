using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>The current user's calendar across every linked Microsoft account. Read live; nothing is stored.</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/calendar")]
    public class CalendarController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader, ILogger<CalendarController> logger) : ControllerBase
    {
        private const int MaxDays = 62;
        private const int MaxEventsPerAccount = 500;

        [HttpGet]
        public async Task<ActionResult<MicrosoftCalendarFeed>> Get([FromQuery] DateTimeOffset start, [FromQuery] DateTimeOffset end, CancellationToken ct)
        {
            if (end <= start || (end - start).TotalDays > MaxDays)
                return BadRequest(new { message = $"Pick a window of up to {MaxDays} days." });

            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var accounts = await linkedAccounts.ListAsync(userId, LinkedProviders.Microsoft, ct);
            var path = $"calendar?start={Uri.EscapeDataString(Iso(start))}&end={Uri.EscapeDataString(Iso(end))}&top={MaxEventsPerAccount}";

            var results = await Task.WhenAll(accounts.Select(account => FetchAsync(userId, account, path, ct)));
            return new MicrosoftCalendarFeed(results.Select(r => r.Account).ToList(), MicrosoftCalendarEvents.Merge(results.Select(r => r.Events)));
        }

        private async Task<(MicrosoftCalendarAccount Account, List<MicrosoftCalendarEvent> Events)> FetchAsync(Guid userId, LinkedAccount account, string path, CancellationToken ct)
        {
            try
            {
                var data = await reader.ReadAsync(userId, account.Id, path, ct);
                return (Summary(account, "ok", null), MicrosoftCalendarEvents.Parse(account.Id, data));
            }
            catch (MicrosoftSignInExpiredException ex)
            {
                return (Summary(account, "reconnect", ex.Message), []);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Calendar read failed for linked Microsoft account {AccountId}", account.Id);
                return (Summary(account, "unavailable", "Microsoft didn't answer for this account. Try again later."), []);
            }
        }

        private static MicrosoftCalendarAccount Summary(LinkedAccount account, string status, string? message) => new(account.Id, account.Email, account.DisplayName, status, message);

        private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}
