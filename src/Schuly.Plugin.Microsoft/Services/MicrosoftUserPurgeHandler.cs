using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>Clears this plugin's data for a deleted Schuly user — a sign-in left in progress. Linked Microsoft accounts live in core and are purged there.</summary>
    public class MicrosoftUserPurgeHandler(PendingLinkStore pending) : IPluginEventHandler<PluginUserPurgeEvent>
    {
        public Task HandleAsync(PluginUserPurgeEvent command, CancellationToken cancellationToken = default)
        {
            pending.Remove(command.ApplicationUserId);
            return Task.CompletedTask;
        }
    }
}
