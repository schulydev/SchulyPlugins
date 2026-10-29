namespace Schuly.Plugin.Schulware.Dtos
{
    public record ReportedAbsenceDto(IReadOnlyList<string> Ids, string DateFrom, string DateTo, string? HourFrom, string? HourTo, string? Reason, string? Comment, IReadOnlyList<string> Profiles, string? ReportedAt);
}
