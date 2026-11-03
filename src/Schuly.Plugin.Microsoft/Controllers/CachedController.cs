using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>The web app's reads through <see cref="MicrosoftResponseCache"/>: the same JSON and sealed ids as the live routes, plus <c>ETag</c>, <c>If-None-Match</c> to 304, <c>Cache-Control: private, no-cache</c> and <c>Age</c>. Every other caller uses the live routes, which never touch the cache.</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft")]
    public class CachedController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader, IdSealer sealer, MicrosoftResponseCache cache) : MicrosoftControllerBase(userContext, linkedAccounts, reader, sealer)
    {
        private const int FirstPage = 25;
        private const string Inbox = "inbox";
        /// <summary>The authentication type of MCP tokens, which the host's in-process plugin calls carry. Helply, MCP and AI apps must read live, so these routes refuse them.</summary>
        private const string McpScheme = "SchulyMcpToken";
        private static readonly Regex IdPattern = new("^[A-Za-z0-9=_\\-+/.]{1,512}$", RegexOptions.Compiled);
        private static readonly Regex ChatIdPattern = new("^[A-Za-z0-9:@._=-]{1,300}$", RegexOptions.Compiled);

        private readonly IPluginUserContext user = userContext;

        /// <summary>Called before signing out: there is no sign-out event for plugins.</summary>
        [HttpDelete("cached")]
        public async Task<IActionResult> Clear(CancellationToken ct = default)
        {
            cache.ClearUser(await user.GetCurrentUserIdAsync(ct));
            return NoContent();
        }

        /// <summary>The first screen in one request: <c>{mail, folders, chats}</c>, each as its own route answers it. The parts are cached on their own, so the list routes share them.</summary>
        [HttpGet("accounts/{accountId:guid}/cached/bootstrap")]
        public Task<IActionResult> Bootstrap(Guid accountId, CancellationToken ct = default) =>
            CachedAsync(accountId, ids => (MicrosoftResponseCache.BootstrapName, CacheTimes.Bootstrap, async _ =>
            {
                var parts = await Task.WhenAll(
                    Part(ids, MailName(Inbox, FirstPage, 0, false), CacheTimes.Lists, AccountsController.MailPath(Inbox, FirstPage, 0, false, null), IdShape.MailMessages),
                    Part(ids, FoldersName, CacheTimes.Folders, "mail/folders", IdShape.MailFolders),
                    Part(ids, ChatsName(FirstPage), CacheTimes.Lists, $"chats?top={FirstPage}", IdShape.Chats));
                return Join(("mail", parts[0].Body), ("folders", parts[1].Body), ("chats", parts[2].Body));
            }), ct);

        [HttpGet("accounts/{accountId:guid}/cached/mail")]
        public Task<IActionResult> Mail(Guid accountId, [FromQuery] string folder = "inbox", [FromQuery] int top = 25, [FromQuery] int skip = 0, [FromQuery] bool unread = false, CancellationToken ct = default) =>
            CachedAsync(accountId, ids =>
            {
                var (opened, size, from) = (AccountsController.OpenFolder(ids, folder), Math.Clamp(top, 1, 500), Math.Clamp(skip, 0, 10000));
                return (MailName(opened, size, from, unread), CacheTimes.Lists, Fetch(ids, AccountsController.MailPath(opened, size, from, unread, null), IdShape.MailMessages));
            }, ct);

        [HttpGet("accounts/{accountId:guid}/cached/mail/folders")]
        public Task<IActionResult> Folders(Guid accountId, CancellationToken ct = default) =>
            CachedAsync(accountId, ids => (FoldersName, CacheTimes.Folders, Fetch(ids, "mail/folders", IdShape.MailFolders)), ct);

        [HttpGet("accounts/{accountId:guid}/cached/mail/{messageId:regex(^(?!folders$).+$)}")]
        public Task<IActionResult> Message(Guid accountId, string messageId, CancellationToken ct = default) =>
            CachedAsync(accountId, ids =>
            {
                var id = OpenSafe(ids, IdKinds.MailMessage, messageId, IdPattern);
                return ($"{MicrosoftResponseCache.MailPrefix}/{id}", CacheTimes.Message, Fetch(ids, $"mail/{Uri.EscapeDataString(id)}", IdShape.MailMessages));
            }, ct);

        [HttpGet("accounts/{accountId:guid}/cached/chats")]
        public Task<IActionResult> Chats(Guid accountId, [FromQuery] int top = 25, CancellationToken ct = default) =>
            CachedAsync(accountId, ids =>
            {
                var size = Math.Clamp(top, 1, 200);
                return (ChatsName(size), CacheTimes.Lists, Fetch(ids, $"chats?top={size}", IdShape.Chats));
            }, ct);

        [HttpGet("accounts/{accountId:guid}/cached/chats/{chatId}/messages")]
        public Task<IActionResult> ChatMessages(Guid accountId, string chatId, [FromQuery] int top = 30, CancellationToken ct = default) =>
            CachedAsync(accountId, ids =>
            {
                var (id, size) = (OpenSafe(ids, IdKinds.Chat, chatId, ChatIdPattern), Math.Clamp(top, 1, 300));
                return ($"{MicrosoftResponseCache.ChatsPrefix}/{id}/messages?top={size}", CacheTimes.ChatMessages, Fetch(ids, $"chats/{Uri.EscapeDataString(id)}/messages?top={size}", IdShape.ChatMessages));
            }, ct);

        private const string FoldersName = MicrosoftResponseCache.MailPrefix + "/folders";

        private static string MailName(string folder, int top, int skip, bool unread) => $"{MicrosoftResponseCache.MailPrefix}?{folder}&{top}&{skip}&{unread}";

        private static string ChatsName(int top) => $"{MicrosoftResponseCache.ChatsPrefix}?{top}";

        private Task<IActionResult> CachedAsync(Guid accountId, Func<IdScope, (string Name, CacheTimes Times, Func<IServiceProvider, Task<byte[]>> Fetch)> target, CancellationToken ct) =>
            RunAsync(accountId, async (ids, c) =>
            {
                if (HttpContext.User.Identity?.AuthenticationType == McpScheme)
                    return NotFound();
                var (name, times, fetch) = target(ids);
                return Reply(await cache.GetAsync(ids.UserId, accountId, name, times, fetch, c));
            }, ct);

        private IActionResult Reply(CachedResponse entry)
        {
            Response.Headers.ETag = entry.ETag;
            Response.Headers.CacheControl = "private, no-cache";
            Response.Headers.Age = cache.AgeSeconds(entry).ToString();
            if (Matches(Request.Headers.IfNoneMatch.ToString(), entry.ETag))
                return StatusCode(StatusCodes.Status304NotModified);
            return File(entry.Body, "application/json");
        }

        private static bool Matches(string ifNoneMatch, string etag) =>
            ifNoneMatch.Trim() == "*" || ifNoneMatch.Split(',').Any(tag => (tag.Trim().StartsWith("W/", StringComparison.Ordinal) ? tag.Trim()[2..] : tag.Trim()) == etag);

        // Scoped services can't be captured: a background refresh outlives the request. The sealer is a singleton, and the ids are the ones the live route would seal.
        private Func<IServiceProvider, Task<byte[]>> Fetch(IdScope ids, string path, IdShape shape) => async services =>
            JsonSerializer.SerializeToUtf8Bytes(ResponseSealer.Seal(await services.GetRequiredService<MicrosoftAccountReader>().ReadAsync(ids.UserId, ids.AccountId, path, CancellationToken.None), shape, ids));

        private Task<CachedResponse> Part(IdScope ids, string name, CacheTimes times, string path, IdShape shape) =>
            cache.GetAsync(ids.UserId, ids.AccountId, name, times, Fetch(ids, path, shape), CancellationToken.None);

        private static byte[] Join(params (string Name, byte[] Body)[] parts)
        {
            using var output = new MemoryStream();
            output.WriteByte((byte)'{');
            for (var n = 0; n < parts.Length; n++)
            {
                output.Write(Encoding.UTF8.GetBytes($"{(n == 0 ? "" : ",")}\"{parts[n].Name}\":"));
                output.Write(parts[n].Body);
            }
            output.WriteByte((byte)'}');
            return output.ToArray();
        }
    }
}
