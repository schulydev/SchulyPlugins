namespace Schuly.Plugin.Schulware.Dtos
{
    public record AbsenceReportRequest(string? DateFrom, string? DateTo, string? HourFrom, string? HourTo, string? Reason, string? Comment);
}
