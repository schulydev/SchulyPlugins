namespace Schulware.Refresher;

public record RefreshRequest(
    string SchulnetzBaseUrl,
    string UserId,
    string? Email = null,
    string? Password = null);

public record RefreshResponse(
    bool Success,
    string? AccessToken = null,
    string? RefreshToken = null,
    string? SessionId = null,
    string? WebSessionUserId = null,
    string? WebSessionTransId = null,
    string? Message = null);

public record SeedCookie(string Name, string Value, string Domain, string Path = "/");

public record SeedRequest(string UserId, List<SeedCookie> Cookies);

public record SeedResponse(bool Success, int CookiesSet = 0, string? Message = null);
