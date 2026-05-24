using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Schuly.Plugin.Schulware.Client;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    internal static class SchulwareApiClientFactory
    {
        public static SchulwareApiClient Create(IHttpClientFactory httpClientFactory, string baseUrl)
        {
            var httpClient = httpClientFactory.CreateClient("Schulware");
            var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient);
            adapter.BaseUrl = baseUrl;
            return new SchulwareApiClient(adapter);
        }
    }
}
