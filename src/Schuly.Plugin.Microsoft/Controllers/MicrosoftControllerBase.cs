using System.Net;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Shared plumbing for the controllers under <c>api/plugins/microsoft/accounts</c>: ownership check, expired sign-in as 409 and upstream error mapping.</summary>
    public abstract class MicrosoftControllerBase(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader) : ControllerBase
    {
        protected MicrosoftAccountReader Reader => reader;

        protected IActionResult Invalid(string message) => BadRequest(new { code = "invalid_request", message });

        protected Task<IActionResult> ReadAsync(Guid accountId, string path, CancellationToken ct) =>
            RunAsync(accountId, async (userId, c) => Ok(await reader.ReadAsync(userId, accountId, path, c)), ct);

        /// <summary>Checks the account is one of the current user's linked Microsoft accounts, runs the action and maps an expired sign-in to 409.</summary>
        protected async Task<IActionResult> RunAsync(Guid accountId, Func<Guid, CancellationToken, Task<IActionResult>> action, CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var account = await linkedAccounts.GetAsync(userId, accountId, ct);
            if (account is null || account.Provider != LinkedProviders.Microsoft)
                return NotFound();

            try
            {
                return await action(userId, ct);
            }
            catch (MicrosoftSignInExpiredException ex)
            {
                // 409, not 401: clients read 401 as their own Schuly session expiring and sign out.
                return Conflict(new { code = "microsoft_signin_expired", message = ex.Message });
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
            {
                return NotFound();
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity)
            {
                // The service validated the request and refused it (bad address, size limit, unsupported attachment).
                return StatusCode((int)ex.StatusCode!.Value, new { code = "invalid_request", message = "Microsoft 365 rejected the request." });
            }
        }
    }
}
