namespace Schuly.Plugin.OdaOrg.Models
{
    /// <summary>Everything one scrape pass pulls from an OdaOrg portal.</summary>
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
        /// <summary>Self-contained data:image/... URI scraped from the profile box.</summary>
        public string? ProfilePictureUrl { get; set; }
    }

    /// <summary>A graded module — final mark from its evaluation page.</summary>
    public class ModuleGrade
    {
        public required string ModuleName { get; set; }
        public decimal FinalGrade { get; set; }
        public decimal? UnroundedGrade { get; set; }
    }

    /// <summary>A single ÜK course day (past or upcoming).</summary>
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
