using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Schuly.Plugin.Schulware.Client;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    internal static class SchulwareApiClientFactory
    {
        /// <summary>
        /// Build a configured Kiota client for SchulwareAPI. Passing
        /// <paramref name="schulnetzBaseUrl"/> attaches it as the
        /// <c>X-Schulnetz-Base-Url</c> header on every outbound request —
        /// SchulwareAPI now requires this header on the mobile / oauth /
        /// websession endpoints to route the call to the right Schulnetz
        /// instance. Pass null when calling endpoints that don't need it
        /// (e.g. /api/app/info).
        /// </summary>
        public static SchulwareApiClient Create(
            IHttpClientFactory httpClientFactory,
            string baseUrl,
            string? schulnetzBaseUrl = null,
            string? bearerToken = null)
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
            var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient);
            adapter.BaseUrl = baseUrl;
            return new SchulwareApiClient(adapter);
        }
    }
}
