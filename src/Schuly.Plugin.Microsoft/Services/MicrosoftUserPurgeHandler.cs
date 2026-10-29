using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>Clears this plugin's data for a deleted Schuly user: a sign-in left in progress and any scheduled chat messages. Linked Microsoft accounts live in core and are purged there.</summary>
    public class MicrosoftUserPurgeHandler(PendingLinkStore pending, ScheduledMessageStore scheduled) : IPluginEventHandler<PluginUserPurgeEvent>
    {
        public async Task HandleAsync(PluginUserPurgeEvent command, CancellationToken cancellationToken = default)
        {
            pending.Remove(command.ApplicationUserId);
            await scheduled.RemoveUserAsync(command.ApplicationUserId, cancellationToken);
        }
    }
}
