using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>
    /// Stores a signed-in Microsoft account as a Schuly linked account. Linking the same account again updates it:
    /// the identity is the Microsoft object id, and rows stored under the email (older links, or a sign-in that
    /// carried no object id) are matched too, so a re-link never creates a duplicate.
    /// </summary>
    public class MicrosoftAccountLinker(ILinkedAccounts linkedAccounts)
    {
        public async Task<LinkedAccount> SaveAsync(Guid userId, string? microsoftUserId, string email, string? name, string refreshToken, string? sessionCookiesJson, CancellationToken ct)
        {
            var existing = await linkedAccounts.ListAsync(userId, LinkedProviders.Microsoft, ct);
            var match = existing.FirstOrDefault(a => !string.IsNullOrEmpty(microsoftUserId) && string.Equals(a.ExternalId, microsoftUserId, StringComparison.OrdinalIgnoreCase))
                ?? existing.FirstOrDefault(a => string.Equals(a.ExternalId, email, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));

            // An existing row keeps its ExternalId (the store matches on it); only a new account gets the object id.
            var externalId = match?.ExternalId ?? (string.IsNullOrWhiteSpace(microsoftUserId) ? email.ToLowerInvariant() : microsoftUserId);
            var account = await linkedAccounts.SaveAsync(userId, new LinkedAccountRecord(LinkedProviders.Microsoft, externalId, email, name ?? match?.DisplayName), ct);
            await linkedAccounts.SetSecretAsync(userId, account.Id, LinkedSecrets.RefreshToken, refreshToken, ct);
            if (sessionCookiesJson is not null)
                await linkedAccounts.SetSecretAsync(userId, account.Id, LinkedSecrets.SessionCookies, sessionCookiesJson, ct);
            return account;
        }
    }
}
