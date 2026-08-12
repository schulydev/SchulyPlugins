namespace Schuly.Plugin.Schulware.Dtos
{
    public record StatelessGradeDto(string? Id, string? ExamId, string? Subject, double? Score, string? Date, string? Comment, double? Points);
}
