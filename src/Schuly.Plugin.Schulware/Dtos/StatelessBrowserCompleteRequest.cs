namespace Schuly.Plugin.Schulware.Dtos
{
    public record StatelessBrowserCompleteRequest(string BaseUrl, string BrowserCallbackUrl, string BrowserContext);
}
