using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Schuly.Plugin.Schulware.Client;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    internal static class SchulwareApiClientFactory
    {
        private const string ServiceKeyHeader = "X-Service-Key";

        private static Uri? _configuredBaseUrl;
        private static string? _serviceKey;

        /// <summary>Set once at plugin load from SchulwareApi:BaseUrl and SchulwareApi:ServiceKey.</summary>
        public static void Configure(string baseUrl, string? serviceKey)
        {
            _configuredBaseUrl = new Uri(baseUrl);
            _serviceKey = string.IsNullOrWhiteSpace(serviceKey) ? null : serviceKey;
        }

        public static SchulwareApiClient Create(IHttpClientFactory httpClientFactory, string baseUrl, string? schulnetzBaseUrl = null, string? bearerToken = null)
        {
            var httpClient = CreateHttpClient(httpClientFactory, baseUrl, schulnetzBaseUrl, bearerToken);
            var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient);
            adapter.BaseUrl = baseUrl;
            return new SchulwareApiClient(adapter);
        }

        /// <summary>The plain HttpClient behind <see cref="Create"/>, with the same bearer, Schulnetz base URL and service key headers.</summary>
        public static HttpClient CreateHttpClient(IHttpClientFactory httpClientFactory, string baseUrl, string? schulnetzBaseUrl = null, string? bearerToken = null)
        {
            var httpClient = httpClientFactory.CreateClient("Schulware");
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            }
            if (!string.IsNullOrWhiteSpace(schulnetzBaseUrl))
            {
                httpClient.DefaultRequestHeaders.Remove("X-Schulnetz-Base-Url");
                httpClient.DefaultRequestHeaders.Add("X-Schulnetz-Base-Url", schulnetzBaseUrl);
            }
            if (_serviceKey is not null && IsConfiguredHost(baseUrl))
                httpClient.DefaultRequestHeaders.Add(ServiceKeyHeader, _serviceKey);
            return httpClient;
        }

        /// <summary>The key only goes to the SchulwareAPI this backend is configured for, never to an account's older or foreign base URL.</summary>
        internal static bool IsConfiguredHost(string baseUrl) =>
            _configuredBaseUrl is not null
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var target)
            && Uri.Compare(target, _configuredBaseUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }
}
