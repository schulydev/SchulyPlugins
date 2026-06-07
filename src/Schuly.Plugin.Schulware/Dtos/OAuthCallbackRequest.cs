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
    ///
    /// <para><b>WebSessionId / WebSessionUserId / WebSessionTransId</b> — the PHP
    /// web session the app read off the cookie jar after the school web login
    /// (PHPSESSID + the id/transid from a dashboard nav link). Server-side code
    /// exchange / Playwright replay both break (Playwright logs out on every
    /// navigation), so the device WebView captures these directly. Powers the
    /// scraper pages (grades, agenda, absences, documents).</para>
    /// </summary>
    public record OAuthCallbackRequest(
        string Code,
        string CodeVerifier,
        string? State,
        string? ContextState,
        string? UserAgent,
        string? WebSessionId = null,
        string? WebSessionUserId = null,
        string? WebSessionTransId = null);
}
