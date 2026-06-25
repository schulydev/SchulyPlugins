namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>An agenda entry for private mode, mapped from SchulwareAPI's mobile event.</summary>
    public record StatelessAgendaEventDto(string? Id, string? Title, string? StartDate, string? EndDate, string? Room, string? Type, string? Comment);
}
