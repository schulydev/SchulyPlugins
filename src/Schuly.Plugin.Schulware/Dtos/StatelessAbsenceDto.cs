namespace Schuly.Plugin.Schulware.Dtos
{
    public record StatelessAbsenceDto(string? Id, string? From, string? To, string? Reason, string? Subject, bool? Excused);
}
