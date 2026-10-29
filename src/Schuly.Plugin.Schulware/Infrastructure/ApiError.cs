namespace Schuly.Plugin.Schulware.Infrastructure
{
    /// <summary>Fixed, generic error bodies for anonymous endpoints and login flows: never exception or upstream text.</summary>
    public static class ApiError
    {
        public const string UnreachableMessage = "The school portal could not be reached.";
        public const string LoginFailedMessage = "Login failed. Check your username and password.";

        public static object Unreachable() => new { code = "provider_unreachable", message = UnreachableMessage };

        public static object InvalidRequest(string message) => new { code = "invalid_request", message };
    }
}
