namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>An absence for private mode, mapped from SchulwareAPI's mobile absence.</summary>
    public record StatelessAbsenceDto(string? Id, string? From, string? To, string? Reason, string? Subject, bool? Excused);
}
