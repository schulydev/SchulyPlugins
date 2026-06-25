namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>A grade for private mode, mapped from SchulwareAPI's mobile grade.</summary>
    public record StatelessGradeDto(string? Id, string? ExamId, string? Subject, double? Score, string? Date, string? Comment, double? Points);
}
