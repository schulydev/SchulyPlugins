using Microsoft.Extensions.Configuration;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    /// <summary>The SchulwareAPI address. It has no default: a missing setting must fail rather than send school logins and tokens to a host the operator didn't choose.</summary>
    public static class SchulwareApiUrl
    {
        public static string Required(IConfiguration configuration)
        {
            var url = configuration["SchulwareApi:BaseUrl"];
            return string.IsNullOrWhiteSpace(url) ? throw new InvalidOperationException("SchulwareApi:BaseUrl is not configured.") : url;
        }
    }
}
