namespace Schuly.Plugin.Schulware.Infrastructure
{
    public class StudentIdCardException(int statusCode, string code, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
        public string Code { get; } = code;

        public static StudentIdCardException NotOffered() => new(404, "not_offered", "This school does not offer the Lernendenausweis in Schulnetz.");
        public static StudentIdCardException SignInExpired() => new(409, "signin_expired", "The Schulnetz sign-in expired. Sign in again.");
        public static StudentIdCardException Unavailable() => new(502, "school_unavailable", "Schulnetz could not be reached. Try again later.");
    }
}
