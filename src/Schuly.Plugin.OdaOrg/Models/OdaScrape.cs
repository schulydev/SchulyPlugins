namespace Schuly.Plugin.OdaOrg.Models
{
    public class OdaScrape
    {
        public OdaProfile? Profile { get; set; }
        public List<ModuleGrade> Grades { get; set; } = new();
        public List<CourseDay> CourseDays { get; set; } = new();
    }

    public class OdaProfile
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Gender { get; set; }
        public DateOnly? Birthday { get; set; }
        public string? Email { get; set; }
        public string? PrivateEmail { get; set; }
        public string? PhoneNumber { get; set; }
        public string? Street { get; set; }
        public string? Zip { get; set; }
        public string? City { get; set; }
        public string? ProfilePictureUrl { get; set; }
    }

    public class ModuleGrade
    {
        public required string ModuleName { get; set; }
        public decimal FinalGrade { get; set; }
        public decimal? UnroundedGrade { get; set; }
    }

    public class CourseDay
    {
        public required string Course { get; set; }
        public string? Topic { get; set; }
        public string? Room { get; set; }
        public DateTime Date { get; set; }
        public DateTime? EndDate { get; set; }
        public string? Instructor { get; set; }
    }
}
