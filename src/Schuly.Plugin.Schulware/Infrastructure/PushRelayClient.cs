using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    /// <summary>SchulwareAPI's push relay (`/api/push`). Off unless SchulwareApi:PushRelayKey and Schuly:PublicBaseUrl are set.</summary>
    public class PushRelayClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        private string? RelayKey => configuration["SchulwareApi:PushRelayKey"];

        /// <summary>Base URL SchulwareAPI can reach this backend on, for the push callback.</summary>
        public string? CallbackBase => configuration["Schuly:PublicBaseUrl"]?.TrimEnd('/');

        public bool Enabled => !string.IsNullOrWhiteSpace(RelayKey) && !string.IsNullOrWhiteSpace(CallbackBase);

        public async Task<(JsonElement Credentials, string Token)> RegisterAsync(string apiBaseUrl, CancellationToken ct)
        {
            using var response = await Client(apiBaseUrl).PostAsync("api/push/register", null, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return (body.GetProperty("credentials").Clone(), body.GetProperty("token").GetString()!);
        }

        public async Task ListenAsync(string apiBaseUrl, string listenerId, JsonElement credentials, string callbackUrl, string secret, CancellationToken ct)
        {
            using var response = await Client(apiBaseUrl).PostAsJsonAsync("api/push/listen", new { listener_id = listenerId, credentials, callback_url = callbackUrl, callback_secret = secret }, ct);
            response.EnsureSuccessStatusCode();
        }

        public async Task StopAsync(string apiBaseUrl, string listenerId, CancellationToken ct)
        {
            using var response = await Client(apiBaseUrl).DeleteAsync($"api/push/listen/{Uri.EscapeDataString(listenerId)}", ct);
            response.EnsureSuccessStatusCode();
        }

        private HttpClient Client(string apiBaseUrl)
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Add("X-Push-Relay-Key", RelayKey);
            return client;
        }
    }
}
