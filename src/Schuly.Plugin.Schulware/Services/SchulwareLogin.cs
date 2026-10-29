using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>Legacy login for app versions that predate the link providers; the work happens in <see cref="ISchulwareConnector"/>.</summary>
    public class SchulwareLogin(IPluginUserContext userContext, ISchulwareConnector connector) : IPluginLogin
    {
        public SchoolSystemDescriptor SchoolSystem => new()
        {
            Key = "schulnetz",
            DisplayName = "Schulnetz",
            LoginMethod = "credentials",
            PrivateAuthStrategy = "token",
            StatelessBasePath = "/api/plugins/schulware/stateless",
            PluginBasePath = "/api/plugins/schulware",
            LoginMethodPath = "/api/plugins/schulware/stateless/login-method",
            SortOrder = 0,
            LoginFields =
            [
                new() { Key = "baseUrl",  Label = "Schulnetz URL", Type = "url",      Placeholder = "https://your-schulnetz.example.ch", Required = true, Suggestions = SchulnetzSchools.All },
                new() { Key = "email",    Label = "Email",         Type = "text",     Required = true, ReplacedByLinkedAccount = true },
                new() { Key = "password", Label = "Password",      Type = "password", Required = true, ReplacedByLinkedAccount = true },
                // Optional: the TOTP *secret* (base32 seed / otpauth:// URI), not a live
                // 6-digit code - SchulwareAPI regenerates the code each login so it works
                // for background refresh too. Left blank, no second factor is sent.
                new() { Key = "totp",     Label = "2FA secret (optional)", Type = "totp", Placeholder = "TOTP secret or otpauth:// URI", Required = false, ReplacedByLinkedAccount = true },
            ],
        };

        public async Task<PluginLoginResult> ConnectAsync(IReadOnlyDictionary<string, string> fields, string? displayName, CancellationToken cancellationToken = default)
        {
            // Opt-out: any explicit "false" disables ongoing background refresh.
            var autoRefresh = !string.Equals(Field(fields, "autoRefresh"), "false", StringComparison.OrdinalIgnoreCase);
            var browser = BrowserSignIn.Parse(Field(fields, BrowserLoginStart.ContextFieldKey));
            var browserCode = browser is null ? null : BrowserSignIn.CodeFrom(Field(fields, BrowserLoginStart.CallbackFieldKey), browser);
            Guid? linkedAccountId = Guid.TryParse(Field(fields, SchoolSystemDescriptor.LinkedAccountFieldKey), out var parsedLinkedId) ? parsedLinkedId : null;
            var userId = await userContext.GetCurrentUserIdAsync(cancellationToken);

            var outcome = await connector.ConnectAsync(userId, new SchulwareConnectRequest(Field(fields, "baseUrl"), Field(fields, "email"), Field(fields, "password"), Field(fields, "totp"), autoRefresh, browser, browserCode, linkedAccountId, displayName, false), cancellationToken);
            return new PluginLoginResult(outcome.Success, outcome.AccountId, outcome.Message);
        }

        private static string? Field(IReadOnlyDictionary<string, string> f, string key)
            => f.TryGetValue(key, out var v) ? v : null;
    }
}
