using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Schuly.Plugin.Microsoft.Infrastructure
{
    public sealed record BrowserStart(string AuthorizeUrl, string RedirectUri, string CodeVerifier);

    public sealed record SignInResult(string Status, string? RefreshToken, string? MicrosoftUserId, string? Email, string? Name, string? Message);

    /// <summary>A read's JSON plus the refresh token to keep (the service rotates it).</summary>
    public sealed record ServiceRead(JsonElement Data, string RefreshToken);

    public class MicrosoftSignInExpiredException() : Exception("The Microsoft sign-in expired; link the account again.");

    /// <summary>Client for SchulyMicrosoft (`MicrosoftApi:BaseUrl`). The service is stateless; this plugin keeps the tokens.</summary>
    public class MicrosoftServiceClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        public const string RefreshTokenHeader = "X-MS-Refresh-Token";

        private string BaseUrl => (configuration["MicrosoftApi:BaseUrl"] ?? throw new InvalidOperationException("MicrosoftApi:BaseUrl is not configured.")).TrimEnd('/') + "/";

        /// <summary>Begin the browser sign-in: the Microsoft authorize URL the app opens in a WebView, plus the code_verifier to keep.</summary>
        public async Task<BrowserStart> StartBrowserLoginAsync(CancellationToken ct)
        {
            using var response = await Client().PostAsync("api/auth/browser/start", null, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return new BrowserStart(body.GetProperty("authorize_url").GetString()!, body.GetProperty("redirect_uri").GetString()!, body.GetProperty("code_verifier").GetString()!);
        }

        /// <summary>Exchange the authorization code the WebView captured for a FOCI refresh token and the account identity.</summary>
        public async Task<SignInResult> CompleteBrowserLoginAsync(string code, string codeVerifier, CancellationToken ct)
        {
            using var response = await Client().PostAsJsonAsync("api/auth/browser/complete", new { code, code_verifier = codeVerifier }, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var account = body.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            return new SignInResult(body.GetProperty("status").GetString()!, Str(body, "refresh_token"), Str(account, "id"), Str(account, "email"), Str(account, "name"), Str(body, "message"));
        }

        /// <summary>GETs `api/{path}` with the account's refresh token; throws <see cref="MicrosoftSignInExpiredException"/> on 401.</summary>
        public async Task<ServiceRead> ReadAsync(string path, string refreshToken, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/{path}");
            request.Headers.Add(RefreshTokenHeader, refreshToken);
            using var response = await Client().SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new MicrosoftSignInExpiredException();
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var rotated = response.Headers.TryGetValues(RefreshTokenHeader, out var values) ? values.FirstOrDefault() : null;
            return new ServiceRead(data, string.IsNullOrEmpty(rotated) ? refreshToken : rotated);
        }

        private HttpClient Client()
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(60);
            return client;
        }

        private static string? Str(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
