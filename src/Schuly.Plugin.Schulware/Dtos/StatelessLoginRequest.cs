namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>
    /// Private-mode credential login payload. SchulwareAPI logs in headlessly
    /// (ms-entrance, no browser) with these and hands back tokens, web session and
    /// the <c>context_state</c> for the caller to persist on-device.
    /// </summary>
    public record StatelessLoginRequest(
        string SchulnetzBaseUrl,
        string Email,
        string Password,
        string? TotpSecret);
}
