using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    public enum ConnectFailure { None, Invalid, Credentials, SignInExpired, Unreachable, Provisioning, BrowserIncomplete }

    public sealed record SchulwareConnectRequest(string? BaseUrl, string? Email, string? Password, string? Totp, bool AutoRefresh, BrowserSignIn.Context? Browser, string? BrowserCode, Guid? LinkedAccountId, string? DisplayName, bool BackgroundSync);

    public sealed record SchulwareConnectOutcome(bool Success, Guid? AccountId, string Message, ConnectFailure Failure);

    /// <summary>The shared connect core: used by the legacy <c>IPluginLogin</c> and the <c>ILinkProvider</c>.</summary>
    public interface ISchulwareConnector
    {
        Task<SchulwareConnectOutcome> ConnectAsync(Guid userId, SchulwareConnectRequest request, CancellationToken cancellationToken = default);
    }
}
