using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Schuly.Plugin.Schulware.Client;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    internal static class SchulwareApiClientFactory
    {
        public static SchulwareApiClient Create(IHttpClientFactory httpClientFactory, string baseUrl, string? schulnetzBaseUrl = null, string? bearerToken = null)
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
