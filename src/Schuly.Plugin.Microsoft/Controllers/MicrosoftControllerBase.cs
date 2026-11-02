using System.Net;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Shared plumbing for the controllers under <c>api/plugins/microsoft/accounts</c>: ownership check, sealed ids (in and out), expired sign-in as 409 and upstream error mapping.</summary>
    public abstract class MicrosoftControllerBase(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader, IdSealer sealer) : ControllerBase
    {
        protected MicrosoftAccountReader Reader => reader;

        protected IActionResult Invalid(string message) => BadRequest(new { code = "invalid_request", message });

        /// <summary>GETs <paramref name="path"/> (built from opened ids) and answers its JSON with every id sealed for this user and account.</summary>
        protected Task<IActionResult> ReadAsync(Guid accountId, Func<IdScope, string> path, IdShape shape, CancellationToken ct) =>
            RunAsync(accountId, async (ids, c) => Ok(ResponseSealer.Seal(await reader.ReadAsync(ids.UserId, accountId, path(ids), c), shape, ids)), ct);

        protected Task<IActionResult> ReadAsync(Guid accountId, string path, IdShape shape, CancellationToken ct) => ReadAsync(accountId, _ => path, shape, ct);

        /// <summary>Checks the account is one of the current user's linked Microsoft accounts, runs the action with the request's id scope, answers 404 for an id that does not open and maps an expired sign-in to 409.</summary>
        protected async Task<IActionResult> RunAsync(Guid accountId, Func<IdScope, CancellationToken, Task<IActionResult>> action, CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var account = await linkedAccounts.GetAsync(userId, accountId, ct);
            if (account is null || account.Provider != LinkedProviders.Microsoft)
                return NotFound();

            try
            {
                return await action(new IdScope(sealer, userId, accountId), ct);
            }
            catch (UnsafeIdException)
            {
                return Invalid("Invalid id.");
            }
            catch (SealedIdException)
            {
                // Wrong user, account or kind, an old key or a tampered id: indistinguishable from an id that does not exist.
                return NotFound();
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
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden)
            {
                // Writing to a notebook the user may only read.
                return StatusCode(403, new { code = "read_only", message = "This notebook is read-only." });
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity or HttpStatusCode.UnsupportedMediaType)
            {
                // The service validated the request and refused it (bad address, size limit, unsupported attachment).
                return StatusCode((int)ex.StatusCode!.Value, new { code = "invalid_request", message = "Microsoft 365 rejected the request." });
            }
        }

        /// <summary>Opens a sealed id and checks the Graph id behind it is still safe as a path segment (pattern and no dot segment): the second layer.</summary>
        protected static string OpenSafe(IdScope ids, string kind, string? sealedId, System.Text.RegularExpressions.Regex pattern)
        {
            var raw = ids.Open(kind, sealedId);
            return PathIds.IsSafe(pattern, raw) ? raw : throw new UnsafeIdException();
        }

        /// <summary>An opened id that failed the path-safety check; <see cref="RunAsync"/> answers 400.</summary>
        protected sealed class UnsafeIdException() : Exception("Invalid id.");
    }
}
