using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    public enum SchulnetzLoginMethod { Credentials, Microsoft, Browser }

    /// <summary>A school's browser sign-in as the plugin needs it: the page to open, the callback to watch for and the opaque context.</summary>
    public sealed record SchulnetzBrowserStart(string AuthUrl, string CallbackUrl, string Context);

    public interface ISchulnetzLoginMethods
    {
        Task<SchulnetzLoginMethod> DetectAsync(string baseUrl, CancellationToken cancellationToken = default);
        Task<SchulnetzBrowserStart?> StartBrowserAsync(string baseUrl, CancellationToken cancellationToken = default);
    }

    /// <summary>Asks SchulwareAPI how a school signs in. Shared by the stateless endpoint and the link provider.</summary>
    public class SchulnetzLoginMethods(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<SchulnetzLoginMethods> logger) : ISchulnetzLoginMethods
    {
        private string ApiBaseUrl => configuration["SchulwareApi:BaseUrl"] ?? "https://schlwr.pianonic.ch";

        public async Task<SchulnetzLoginMethod> DetectAsync(string baseUrl, CancellationToken cancellationToken = default)
        {
            try
            {
                var client = SchulwareApiClientFactory.Create(httpClientFactory, ApiBaseUrl, baseUrl.TrimEnd('/'));
                var result = await client.Api.Authenticate.LoginMethod.GetAsync(cancellationToken: cancellationToken);
                return result?.Method switch
                {
                    LoginMethodDto_method.Microsoft => SchulnetzLoginMethod.Microsoft,
                    LoginMethodDto_method.Browser => SchulnetzLoginMethod.Browser,
                    _ => SchulnetzLoginMethod.Credentials,
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not detect the Schulnetz login method; falling back to credentials");
                return SchulnetzLoginMethod.Credentials;
            }
        }

        public async Task<SchulnetzBrowserStart?> StartBrowserAsync(string baseUrl, CancellationToken cancellationToken = default)
        {
            try
            {
                var client = SchulwareApiClientFactory.Create(httpClientFactory, ApiBaseUrl);
                var start = await client.Api.Authenticate.Browser.Start.PostAsync(new BrowserLoginStartRequestDto { SchulnetzBaseUrl = baseUrl.TrimEnd('/') }, cancellationToken: cancellationToken);
                if (start?.AuthUrl is null || start.CallbackUrl is null || start.CodeVerifier is null || start.DeviceId is null || start.State is null)
                    return null;
                return new SchulnetzBrowserStart(start.AuthUrl, start.CallbackUrl, BrowserSignIn.Serialize(new BrowserSignIn.Context(start.CodeVerifier, start.DeviceId, start.State)));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Browser sign-in start failed");
                return null;
            }
        }
    }
}
