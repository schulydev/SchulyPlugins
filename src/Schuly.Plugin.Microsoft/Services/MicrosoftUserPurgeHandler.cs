using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>Clears this plugin's data for a deleted Schuly user: a sign-in left in progress, any scheduled chat messages and the answers held in memory. Linked Microsoft accounts live in core and are purged there.</summary>
    public class MicrosoftUserPurgeHandler(PendingLinkStore pending, ScheduledMessageStore scheduled, MicrosoftResponseCache cache) : IPluginEventHandler<PluginUserPurgeEvent>
    {
        public async Task HandleAsync(PluginUserPurgeEvent command, CancellationToken cancellationToken = default)
        {
            cache.ClearUser(command.ApplicationUserId);
            pending.RemoveLatest(command.ApplicationUserId);
            await scheduled.RemoveUserAsync(command.ApplicationUserId, cancellationToken);
        }
    }
}
