using System.Net;
using Microsoft.AspNetCore.Http;
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
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests)
            {
                // Microsoft is throttling this account. The service's own wait already ran out, so the caller should back off.
                Response.Headers.RetryAfter = "60";
                return StatusCode(429, new { code = "throttled", message = "Microsoft is limiting requests for this account. Try again in a minute." });
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

        /// <summary>Images go on the photo lane and its cache; the browser may keep them an hour (this response carries no token).</summary>
        protected Task<IActionResult> ImageAsync(Guid accountId, Func<IdScope, CancellationToken, Task<PhotoResult>> fetch, CancellationToken ct) =>
            RunAsync(accountId, async (ids, c) =>
            {
                var image = await fetch(ids, c);
                if (image.Bytes is null)
                    return NotFound();
                Response.Headers.CacheControl = "private, max-age=3600";
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(image.Bytes, image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !image.ContentType.Contains("svg", StringComparison.OrdinalIgnoreCase) ? image.ContentType : "image/jpeg");
            }, ct);

        protected Task<IActionResult> PdfAsync(Guid accountId, Func<IdScope, CancellationToken, Task<HttpResponseMessage?>> open, CancellationToken ct) =>
            RunAsync(accountId, async (ids, c) =>
            {
                HttpResponseMessage? upstream;
                try
                {
                    upstream = await open(ids, c);
                }
                catch (Exception ex) when (IsUploadTooLarge(ex))
                {
                    return PdfTooLarge();
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.UnsupportedMediaType)
                {
                    return PdfUnsupported();
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.RequestEntityTooLarge)
                {
                    return PdfTooLarge();
                }
                catch (HttpRequestException ex) when (ex.StatusCode is null or >= HttpStatusCode.InternalServerError)
                {
                    return PdfFailed();
                }
                if (upstream is null)
                    return NotFound();
                HttpContext.Response.RegisterForDispose(upstream);
                // Only a PDF is ever served inline; anything else the service could answer is refused.
                if (!string.Equals(upstream.Content.Headers.ContentType?.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                    return PdfFailed();
                AttachmentResponse.ApplyInlinePdf(Response, upstream.Content.Headers.ContentDisposition?.ToString());
                if (upstream.Content.Headers.ContentLength is { } length)
                    Response.ContentLength = length;
                return File(await upstream.Content.ReadAsStreamAsync(c), "application/pdf", enableRangeProcessing: false);
            }, ct);

        protected static bool IsUploadTooLarge(Exception? ex)
        {
            for (; ex is not null; ex = ex.InnerException)
                if (ex is LengthLimitedStream.TooLargeException || (ex is BadHttpRequestException bad && bad.StatusCode == StatusCodes.Status413PayloadTooLarge))
                    return true;
            return false;
        }

        protected ObjectResult PdfUnsupported() =>
            StatusCode(StatusCodes.Status415UnsupportedMediaType, new { code = "unsupported_type", message = "This file type cannot be converted to PDF." });

        protected IActionResult PdfTooLarge() =>
            StatusCode(StatusCodes.Status413PayloadTooLarge, new { code = "invalid_request", message = "The file is larger than the conversion limit." });

        protected ObjectResult PdfFailed() =>
            StatusCode(StatusCodes.Status502BadGateway, new { code = "conversion_failed", message = "Microsoft 365 could not convert the file." });
    }
}
