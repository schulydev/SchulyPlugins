using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>Sends scheduled chat messages once they are due, then forgets them. A transient failure is retried for a short while; anything else is dropped.</summary>
    public sealed class ScheduledMessageTask : IPluginBackgroundTask
    {
        private const int MaxAttempts = 5;
        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(30);

        public string Name => "Microsoft scheduled chat messages";
        public PluginSchedule Schedule => PluginSchedule.Every(TimeSpan.FromMinutes(1));

        public async Task ExecuteAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            using var scope = serviceProvider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ScheduledMessageStore>();
            var reader = scope.ServiceProvider.GetRequiredService<MicrosoftAccountReader>();
            var linkedAccounts = scope.ServiceProvider.GetRequiredService<ILinkedAccounts>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<ScheduledMessageTask>>();

            var now = DateTimeOffset.UtcNow;
            foreach (var message in await store.TakeDueAsync(now, cancellationToken))
            {
                try
                {
                    var account = await linkedAccounts.GetAsync(message.UserId, message.AccountId, cancellationToken);
                    if (account is null || account.Provider != LinkedProviders.Microsoft)
                        continue;
                    object body = message.Importance is null ? new { content = message.Content, contentType = message.ContentType } : new { content = message.Content, contentType = message.ContentType, importance = message.Importance };
                    await reader.PostAsync(message.UserId, message.AccountId, $"chats/{Uri.EscapeDataString(message.ChatId)}/messages", body, cancellationToken);
                }
                catch (Exception ex) when (Transient(ex) && message.Attempts + 1 < MaxAttempts && now - message.SendAt < GiveUpAfter)
                {
                    logger.LogInformation("Scheduled chat message {Id} will be retried: {Error}", message.Id, ex.GetType().Name);
                    await store.AddAsync(message with { Attempts = message.Attempts + 1 }, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Never the content: only the id and the failure kind reach the log.
                    logger.LogWarning("Scheduled chat message {Id} was dropped: {Error}", message.Id, ex.GetType().Name);
                }
            }
        }

        private static bool Transient(Exception ex) =>
            ex is HttpRequestException { StatusCode: null or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError } or TaskCanceledException;
    }
}
