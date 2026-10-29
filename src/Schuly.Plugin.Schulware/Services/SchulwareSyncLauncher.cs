using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Runs the first sync after a link in the background, in a fresh scope from the plugin's root provider, so the
    /// link request returns right away. Registered as a singleton so it receives the root provider.
    /// </summary>
    public sealed class SchulwareSyncLauncher(IServiceProvider root, SchulwareSyncTask syncTask, ILogger<SchulwareSyncLauncher> logger)
    {
        private readonly ConcurrentDictionary<Guid, Task> running = new();

        public bool IsRunning(Guid accountId) => running.ContainsKey(accountId);

        public void Launch(Guid accountId)
        {
            running.GetOrAdd(accountId, id => Task.Run(async () =>
            {
                try
                {
                    await syncTask.SyncAccountAsync(id, root, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Initial sync failed for {AccountId}", id);
                }
                finally
                {
                    running.TryRemove(id, out _);
                }
            }));
        }
    }
}
