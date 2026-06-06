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
    /// <para><b>WebSessionId / WebSessionUserId / WebSessionTransId</b> — the
    /// Schulnetz PHP web session the app captured straight from the WebView
    /// after login (PHPSESSID + the <c>id</c>/<c>transid</c> URL params read off
    /// a dashboard nav link). These power the scraper-only pages (documents /
    /// report cards). The OAuth code itself can't be redeemed server-side — it's
    /// bound to the browser's MS cookies — so the app must hand these over
    /// directly. Grades/agenda/absences keep using the Mobile API regardless.</para>
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
