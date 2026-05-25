namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>
    /// OAuth callback payload from the mobile app.
    ///
    /// <para><b>ContextState</b> — Playwright <c>storage_state</c> snapshot the
    /// app built by scraping the WebView's cookies + per-origin localStorage
    /// across the SSO chain (MS → Schulnetz). Persisted so the stateless
    /// <c>/api/authenticate/refresh</c> endpoint can replay the SSO without
    /// any user prompt.</para>
    ///
    /// <para><b>UserAgent</b> — the exact UA string the WebView used. MS binds
    /// session cookies to UA; replays must match.</para>
    /// </summary>
    public record OAuthCallbackRequest(
        string Code,
        string CodeVerifier,
        string? State,
        string? ContextState,
        string? UserAgent);
}
