using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Registers an account for Schulnetz push notifications: Firebase credentials from the
    /// SchulwareAPI relay, the device filed at Schulnetz, and a relay listener that calls back
    /// into <c>PushController</c>. The credentials, callback secret and device id live in the vault.
    /// </summary>
    public class PushRegistrationService(PushRelayClient relay, IPluginVault vault, IHttpClientFactory httpClientFactory, ILogger<PushRegistrationService> logger)
    {
        private sealed record PushState(JsonElement Credentials, string Secret, string? DeviceId);

        private static string Key(Guid accountId) => $"push:{accountId}";

        public async Task EnsureAsync(SchulwareAccount account, CancellationToken ct)
        {
            if (!relay.Enabled || account.MobileAccessToken is null)
                return;

            var state = Load(account.Id);
            if (state is null)
            {
                var (credentials, token) = await relay.RegisterAsync(account.SchulwareApiBaseUrl, ct);
                var client = SchulwareApiClientFactory.Create(httpClientFactory, account.SchulwareApiBaseUrl, account.SchulnetzBaseUrl, account.MobileAccessToken);
                var device = await client.Api.Mobile.Notifications.Token.PostAsync(new PushTokenRequestDto { Token = token, Type = "schulNetz-App-FCM-PROD" }, cancellationToken: ct);
                if (device?.Status != "0" || string.IsNullOrEmpty(device.Id))
                {
                    logger.LogWarning("Schulnetz refused the push device for {AccountId}: {Message}", account.Id, device?.Message);
                    return;
                }
                state = new PushState(credentials, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), device.Id);
                Save(account.Id, state);
                logger.LogInformation("Registered push device {DeviceId} for account {AccountId}", device.Id, account.Id);
            }

            await relay.ListenAsync(account.SchulwareApiBaseUrl, account.Id.ToString(), state.Credentials, $"{relay.CallbackBase}/api/plugins/schulware/push/{account.Id}", state.Secret, ct);
        }

        public async Task RemoveAsync(SchulwareAccount account, CancellationToken ct)
        {
            var state = Load(account.Id);
            if (state is null)
                return;

            try
            {
                if (relay.Enabled)
                    await relay.StopAsync(account.SchulwareApiBaseUrl, account.Id.ToString(), ct);
                if (state.DeviceId is not null && account.MobileAccessToken is not null)
                {
                    var client = SchulwareApiClientFactory.Create(httpClientFactory, account.SchulwareApiBaseUrl, account.SchulnetzBaseUrl, account.MobileAccessToken);
                    await client.Api.Mobile.Notifications.Token[state.DeviceId].DeleteAsync(cancellationToken: ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Removing the push device for {AccountId} failed", account.Id);
            }
            vault.Remove(Key(account.Id));
        }

        /// <summary>True when <paramref name="signature"/> is the account's HMAC-SHA256 of <paramref name="body"/>.</summary>
        public bool IsValidSignature(Guid accountId, byte[] body, string? signature)
        {
            var state = Load(accountId);
            if (state is null || string.IsNullOrEmpty(signature))
                return false;
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(state.Secret), body);
            return signature.Length == expected.Length * 2 && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), expected);
        }

        private PushState? Load(Guid accountId) =>
            vault.TryGet(Key(accountId), out var json) ? JsonSerializer.Deserialize<PushState>(json) : null;

        private void Save(Guid accountId, PushState state) => vault.Set(Key(accountId), JsonSerializer.Serialize(state));
    }
}
