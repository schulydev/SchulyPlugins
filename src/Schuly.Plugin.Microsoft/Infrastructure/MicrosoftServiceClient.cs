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

    /// <summary>A binary read (null <see cref="Bytes"/> when the service answered 404) plus the refresh token to keep.</summary>
    public sealed record ServiceBinary(byte[]? Bytes, string ContentType, string RefreshToken, string? ContentDisposition = null);

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
            using var request = new HttpRequestMessage(HttpMethod.Get, PathIds.Upstream(new Uri(BaseUrl), path));
            request.Headers.Add(RefreshTokenHeader, refreshToken);
            using var response = await Client().SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new MicrosoftSignInExpiredException();
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var rotated = response.Headers.TryGetValues(RefreshTokenHeader, out var values) ? values.FirstOrDefault() : null;
            return new ServiceRead(data, string.IsNullOrEmpty(rotated) ? refreshToken : rotated);
        }

        /// <summary>POSTs a JSON body to `api/{path}`; <see cref="ServiceRead.Data"/> is default (Undefined) when the service answers with no content.</summary>
        public async Task<ServiceRead> PostJsonAsync(string path, object body, string refreshToken, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, PathIds.Upstream(new Uri(BaseUrl), path)) { Content = JsonContent.Create(body) };
            request.Headers.Add(RefreshTokenHeader, refreshToken);
            using var response = await Client().SendAsync(request, ct);
            var rotated = Rotated(response, refreshToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new MicrosoftSignInExpiredException();
            response.EnsureSuccessStatusCode();
            var data = response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return new ServiceRead(data, rotated);
        }

        /// <summary>POSTs with no body to `api/{path}` (204-style actions) and returns the token to keep.</summary>
        public async Task<string> PostEmptyAsync(string path, string refreshToken, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, PathIds.Upstream(new Uri(BaseUrl), path));
            request.Headers.Add(RefreshTokenHeader, refreshToken);
            using var response = await Client().SendAsync(request, ct);
            var rotated = Rotated(response, refreshToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new MicrosoftSignInExpiredException();
            response.EnsureSuccessStatusCode();
            return rotated;
        }

        /// <summary>GETs raw bytes from `api/{path}`; a 404 yields null bytes (still returning the rotated token).</summary>
        public async Task<ServiceBinary> GetBytesAsync(string path, string refreshToken, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, PathIds.Upstream(new Uri(BaseUrl), path));
            request.Headers.Add(RefreshTokenHeader, refreshToken);
            using var response = await Client().SendAsync(request, ct);
            var rotated = Rotated(response, refreshToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new MicrosoftSignInExpiredException();
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new ServiceBinary(null, "", rotated);
            response.EnsureSuccessStatusCode();
            var disposition = response.Content.Headers.TryGetValues("Content-Disposition", out var dispositions) ? dispositions.FirstOrDefault() : null;
            return new ServiceBinary(await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "", rotated, disposition);
        }

        /// <summary>DELETEs `api/{path}` (204-style) and returns the token to keep.</summary>
        public async Task<string> DeleteAsync(string path, string refreshToken, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, PathIds.Upstream(new Uri(BaseUrl), path));
            request.Headers.Add(RefreshTokenHeader, refreshToken);
            using var response = await Client().SendAsync(request, ct);
            var rotated = Rotated(response, refreshToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new MicrosoftSignInExpiredException();
            response.EnsureSuccessStatusCode();
            return rotated;
        }

        /// <summary>POSTs prepared content (multipart uploads) to `api/{path}` with a long timeout, streaming the body; returns the JSON answer and the token to keep.</summary>
        public async Task<ServiceRead> PostContentAsync(string path, HttpContent content, string refreshToken, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, PathIds.Upstream(new Uri(BaseUrl), path)) { Content = content };
            request.Headers.Add(RefreshTokenHeader, refreshToken);
            using var response = await Client(UploadTimeout).SendAsync(request, ct);
            var rotated = Rotated(response, refreshToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new MicrosoftSignInExpiredException();
            response.EnsureSuccessStatusCode();
            return new ServiceRead(await response.Content.ReadFromJsonAsync<JsonElement>(ct), rotated);
        }

        /// <summary>GETs `api/{path}` and hands back the open response (headers read, body not yet) for streaming; null on 404. The caller disposes it.</summary>
        public async Task<(HttpResponseMessage? Response, string RefreshToken)> OpenStreamAsync(string path, string refreshToken, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, PathIds.Upstream(new Uri(BaseUrl), path));
            request.Headers.Add(RefreshTokenHeader, refreshToken);
            var response = await Client(UploadTimeout).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var rotated = Rotated(response, refreshToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new MicrosoftSignInExpiredException();
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                return (null, rotated);
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"SchulyMicrosoft answered {(int)status}.", null, status);
            }
            return (response, rotated);
        }

        private static string Rotated(HttpResponseMessage response, string current)
        {
            var rotated = response.Headers.TryGetValues(RefreshTokenHeader, out var values) ? values.FirstOrDefault() : null;
            return string.IsNullOrEmpty(rotated) ? current : rotated;
        }

        private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(15);

        private HttpClient Client() => Client(TimeSpan.FromSeconds(60));

        private HttpClient Client(TimeSpan timeout)
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(BaseUrl);
            client.Timeout = timeout;
            return client;
        }

        private static string? Str(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
