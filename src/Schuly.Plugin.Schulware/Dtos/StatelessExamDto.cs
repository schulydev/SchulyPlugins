namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>An exam/event for private mode, mapped from SchulwareAPI's mobile exam.</summary>
    public record StatelessExamDto(
        string? Id,
        string? Name,
        string? Subject,
        string? StartDate,
        string? EndDate,
        string? Room,
        string? Comment,
        string? Type);
}
