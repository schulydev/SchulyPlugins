using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware.Controllers
{
    /// <summary>Callback for the SchulwareAPI push relay: a verified Schulnetz push syncs that account right away.</summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/plugins/schulware/push/{accountId:guid}")]
    public class PushController(PushRegistrationService push, SchulwareSyncTask syncTask, IServiceScopeFactory scopes, ILogger<PushController> logger) : ControllerBase
    {
        private static readonly ConcurrentDictionary<Guid, byte> Syncing = new();

        [HttpPost]
        public async Task<IActionResult> Receive(Guid accountId)
        {
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer);
            if (!push.IsValidSignature(accountId, buffer.ToArray(), Request.Headers["X-Schulware-Signature"]))
                return Unauthorized();

            logger.LogInformation("Schulnetz push for account {AccountId}; syncing", accountId);
            if (Syncing.TryAdd(accountId, 0))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await syncTask.SyncAccountAsync(accountId, scope.ServiceProvider);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Push-triggered sync failed for {AccountId}", accountId);
                    }
                    finally
                    {
                        Syncing.TryRemove(accountId, out _);
                    }
                });
            }
            return Accepted();
        }
    }
}
