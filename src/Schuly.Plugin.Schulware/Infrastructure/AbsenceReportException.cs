namespace Schuly.Plugin.Schulware.Infrastructure
{
    /// <summary>A failed absence-reporting call, carrying the HTTP status and machine-readable code the endpoint answers with.</summary>
    public class AbsenceReportException(int statusCode, string code, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
        public string Code { get; } = code;

        public static AbsenceReportException SignInExpired() => new(409, "signin_expired", "The Schulnetz sign-in expired. Sign in again.");
        public static AbsenceReportException Unavailable() => new(502, "school_unavailable", "Schulnetz could not be reached. Try again later.");
        public static AbsenceReportException Invalid(string message) => new(400, "invalid_request", message);
        public static AbsenceReportException Rejected(string? message) => new(400, "school_rejected", string.IsNullOrWhiteSpace(message) ? "Schulnetz rejected the request." : message);
        public static AbsenceReportException NotAllowed() => new(403, "absence_reporting_not_allowed", "Absence reporting is not enabled for this Schulnetz account.");
        public static AbsenceReportException NotWithdrawable() => new(400, "not_withdrawable", "Only absences that are still open can be withdrawn.");
    }
}
