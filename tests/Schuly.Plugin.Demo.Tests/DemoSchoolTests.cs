using System.Text;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Demo.Services;
using Xunit;

namespace Schuly.Plugin.Demo.Tests
{
    public class DemoSchoolTests
    {
        /// <summary>Every 5th day of a year plus the edges where semesters, weeks and daylight saving time change.</summary>
        public static TheoryData<DateTimeOffset> Nows()
        {
            var data = new TheoryData<DateTimeOffset>();
            var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
            for (var d = 0; d < 366; d += 5)
                data.Add(start.AddDays(d));
            foreach (var edge in new[] { "2026-07-31T21:59:00Z", "2026-07-31T22:01:00Z", "2027-01-31T23:30:00Z", "2026-03-29T00:30:00Z", "2026-10-25T00:30:00Z", "2026-11-15T22:30:00Z", "2026-11-16T05:00:00Z" })
                data.Add(DateTimeOffset.Parse(edge));
            return data;
        }

        [Theory]
        [MemberData(nameof(Nows))]
        public void Ids_and_exam_names_never_depend_on_the_date(DateTimeOffset now)
        {
            var reference = DemoSchool.Build(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero));
            var snapshot = DemoSchool.Build(now);

            Assert.Equal(reference.Grades.Select(g => g.ExternalId).Order(), snapshot.Grades.Select(g => g.ExternalId).Order());
            Assert.Equal(reference.Agenda.Select(a => a.ExternalId).Order(), snapshot.Agenda.Select(a => a.ExternalId).Order());
            Assert.Equal(reference.Absences.Select(a => a.ExternalId).Order(), snapshot.Absences.Select(a => a.ExternalId).Order());
            Assert.Equal(reference.Exams.Select(e => (e.Subject, e.Name)).Order(), snapshot.Exams.Select(e => (e.Subject, e.Name)).Order());
        }

        [Theory]
        [MemberData(nameof(Nows))]
        public void Ids_are_unique_and_exams_match_their_grades(DateTimeOffset now)
        {
            var s = DemoSchool.Build(now);

            Assert.Equal(s.Grades.Count, s.Grades.Select(g => g.ExternalId).Distinct().Count());
            Assert.Equal(s.Agenda.Count, s.Agenda.Select(a => a.ExternalId).Distinct().Count());
            Assert.Equal(s.Absences.Count, s.Absences.Select(a => a.ExternalId).Distinct().Count());
            Assert.Equal(s.Exams.Count, s.Exams.Select(e => (e.Subject, e.Name)).Distinct().Count());
            foreach (var g in s.Grades)
                Assert.Contains(s.Exams, e => e.Subject == g.Subject && e.Name == g.ExamName && e.Date == g.ExamDate);
        }

        [Theory]
        [MemberData(nameof(Nows))]
        public void Grades_are_in_the_past_and_fall_into_two_semesters(DateTimeOffset now)
        {
            var s = DemoSchool.Build(now);
            var today = DemoSchool.Today(now);
            var anchor = DemoSchool.Anchor(today);

            Assert.Equal(8, s.Grades.Select(g => g.Subject).Distinct().Count());
            Assert.All(s.Grades, g =>
            {
                Assert.True(g.ExamDate < today, $"{g.ExamName} on {g.ExamDate} is not before {today}");
                Assert.InRange(g.Score, 1m, 6m);
                Assert.Equal(g.Score, Math.Round(g.Score, 1));
            });

            Assert.True(s.Grades.GroupBy(g => (g.Subject, g.ExternalId.Split('|')[1])).Count(g => g.Select(x => x.Score).Distinct().Count() > 1) >= 12, "exam marks within a subject should vary");
            Assert.True(s.Grades.Select(g => g.Score).Distinct().Count() >= 15);

            var current = s.Grades.Where(g => g.ExternalId.Contains("|current|")).ToList();
            var previous = s.Grades.Where(g => g.ExternalId.Contains("|previous|")).ToList();
            Assert.Equal(24, current.Count);
            Assert.Equal(24, previous.Count);
            Assert.All(current, g => Assert.True(anchor.Contains(g.ExamDate!.Value), $"{g.ExamDate} not in {anchor.Label}"));
            Assert.All(previous, g => Assert.True(anchor.Previous.Contains(g.ExamDate!.Value), $"{g.ExamDate} not in {anchor.Previous.Label}"));
        }

        [Theory]
        [MemberData(nameof(Nows))]
        public void Upcoming_exams_are_ahead_and_on_the_timetable(DateTimeOffset now)
        {
            var s = DemoSchool.Build(now);
            var today = DemoSchool.Today(now);
            var graded = s.Grades.Select(g => (g.Subject, g.ExamName)).ToHashSet();
            var upcoming = s.Exams.Where(e => !graded.Contains((e.Subject, e.Name))).ToList();

            Assert.Equal(5, upcoming.Count);
            Assert.All(upcoming, e =>
            {
                Assert.True(e.Date > today && e.Date <= today.AddDays(28), $"{e.Name} on {e.Date}");
                Assert.Contains(s.Agenda, a => a.Kind == AgendaKind.Test && a.Title == $"{e.Subject}: {e.Name}" && DateOnly.FromDateTime(a.Start.UtcDateTime) == e.Date);
            });
        }

        [Theory]
        [MemberData(nameof(Nows))]
        public void Timetable_covers_this_week_and_the_next(DateTimeOffset now)
        {
            var s = DemoSchool.Build(now);
            var today = DemoSchool.Today(now);
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var lessonDays = s.Agenda.Where(a => a.Kind is AgendaKind.Lesson or AgendaKind.Test).Select(a => DateOnly.FromDateTime(a.Start.UtcDateTime)).ToHashSet();

            foreach (var offset in new[] { 0, 1, 3, 7, 8, 10 })
                Assert.Contains(monday.AddDays(offset), lessonDays);
            Assert.All(s.Agenda, a => Assert.True(a.End > a.Start));
        }

        [Fact]
        public void Lessons_are_in_Swiss_time_across_daylight_saving()
        {
            var summer = DemoSchool.Build(new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
            var winter = DemoSchool.Build(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero));

            Assert.Contains(summer.Agenda, a => a.Kind == AgendaKind.Lesson && a.Start.UtcDateTime.TimeOfDay == new TimeSpan(6, 0, 0));
            Assert.Contains(winter.Agenda, a => a.Kind == AgendaKind.Lesson && a.Start.UtcDateTime.TimeOfDay == new TimeSpan(7, 0, 0));
        }

        [Theory]
        [MemberData(nameof(Nows))]
        public void Absences_cover_every_state_the_app_shows(DateTimeOffset now)
        {
            var s = DemoSchool.Build(now);
            var today = DemoSchool.Today(now);
            var a = s.Absences.ToDictionary(x => x.ExternalId.Split('|')[1]);

            Assert.All(s.Absences, x => Assert.True(DateOnly.FromDateTime(x.From.UtcDateTime) < today));
            Assert.Equal("open", a["arzttermin"].TrainerStatus);
            Assert.False(a["arzttermin"].IsExcused);
            Assert.False(a["krank-offen"].IsExcused);
            Assert.True(a["krank-offen"].ExcuseDeadline >= today, "the open absence must still be excusable");
            Assert.True(a["zug"].IsLate);
            Assert.True(a["krank-entschuldigt"].IsExcused);
            Assert.Equal("confirmed", a["familie"].TrainerStatus);
            Assert.False(a["familie"].IsExcused);
            Assert.True(a["unentschuldigt"].ExcuseDeadline < today, "the unexcused absence must be past its deadline");
            Assert.False(a["unentschuldigt"].IsExcused);
        }

        [Theory]
        [MemberData(nameof(Nows))]
        public void Reports_match_the_grades_with_half_point_rounding(DateTimeOffset now)
        {
            var s = DemoSchool.Build(now);
            var anchor = DemoSchool.Anchor(DemoSchool.Today(now));
            var last = s.Reports[^1];

            Assert.InRange(s.Reports.Count, 2, 3);
            Assert.Equal((anchor.Previous.YearStart, anchor.Previous.Half), (last.SchoolYearStart, last.SemesterHalf));
            foreach (var subject in last.Subjects)
            {
                var grades = s.Grades.Where(g => g.ExternalId.Contains("|previous|") && g.Subject == subject.SubjectName).Select(g => (g.Score, g.Weight));
                Assert.Equal(DemoSchool.SubjectGrade(grades), subject.Grade);
                Assert.Equal(0m, subject.Grade!.Value * 2 % 1);
            }
            Assert.Equal(Math.Round(last.Subjects.Average(x => x.Grade!.Value), 1, MidpointRounding.AwayFromZero), last.GradeAverage);
        }

        [Theory]
        [InlineData(new[] { 4.0, 4.5, 5.0 }, new[] { 1.0, 1.0, 2.0 }, 4.5)]
        [InlineData(new[] { 4.2, 4.3 }, new[] { 1.0, 1.0 }, 4.5)]
        [InlineData(new[] { 4.7 }, new[] { 1.0 }, 4.5)]
        [InlineData(new[] { 4.75 }, new[] { 1.0 }, 5.0)]
        [InlineData(new[] { 3.6, 3.8 }, new[] { 1.0, 1.0 }, 3.5)]
        public void Subject_grade_rounds_the_weighted_average_to_a_half(double[] scores, double[] weights, double expected)
        {
            var exams = scores.Zip(weights, (s, w) => ((decimal)s, (decimal)w));
            Assert.Equal((decimal)expected, DemoSchool.SubjectGrade(exams));
        }

        [Fact]
        public void Data_is_fictional()
        {
            var s = DemoSchool.Build(DateTimeOffset.UtcNow);
            Assert.EndsWith(".example", s.Student.Email);
            Assert.Null(s.School.Website);
            Assert.Null(s.School.LogoUrl);
            Assert.Null(s.Student.ProfilePictureUrl);
        }

        [Fact]
        public void Pdf_is_well_formed()
        {
            var pdf = DemoPdf.Render("Schulbestätigung (Test)", ["Zeile ä ö ü", "Gedankenstrich – ersetzt"]);
            var text = Encoding.Latin1.GetString(pdf);

            Assert.StartsWith("%PDF-1.4", text);
            Assert.EndsWith("%%EOF\n", text);
            var startxref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
            Assert.StartsWith("xref", text[startxref..]);
            Assert.Contains("Schulbestätigung \\(Test\\)", text);
            foreach (var n in Enumerable.Range(1, 5))
            {
                var entry = text[(startxref + "xref\n0 6\n".Length + 20 * n)..][..10];
                Assert.StartsWith($"{n} 0 obj", text[int.Parse(entry)..]);
            }
        }
    }
}
