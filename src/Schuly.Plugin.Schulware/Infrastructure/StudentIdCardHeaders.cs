using Microsoft.AspNetCore.Http;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    public static class StudentIdCardHeaders
    {
        /// <summary>The card is personal; no browser, proxy or CDN may keep a copy.</summary>
        public static void NoStore(HttpResponse response)
        {
            response.Headers.CacheControl = "no-store, private";
            response.Headers.Pragma = "no-cache";
        }
    }
}
