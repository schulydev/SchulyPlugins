using System.Text;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Demo.Services
{
    /// <summary>A Swiss semester: the 1st runs August to January, the 2nd February to July.</summary>
    public readonly record struct Semester(int YearStart, int Half)
    {
        public DateOnly Start => Half == 1 ? new(YearStart, 8, 1) : new(YearStart + 1, 2, 1);
        public DateOnly End => Half == 1 ? new(YearStart + 1, 1, 31) : new(YearStart + 1, 7, 31);
        public Semester Previous => Half == 2 ? new(YearStart, 1) : new(YearStart - 1, 2);
        public string Label => $"{Half}. Semester {YearStart % 100:00}/{(YearStart + 1) % 100:00}";

        public static Semester Of(DateOnly day) => day.Month >= 8 ? new(day.Year, 1) : day.Month == 1 ? new(day.Year - 1, 1) : new(day.Year - 1, 2);

        public bool Contains(DateOnly day) => day >= Start && day <= End;
    }

    /// <summary>A document the demo school hands out; <see cref="Lines"/> become a one-page PDF.</summary>
    public sealed record DemoDocument(string ExternalId, string Title, string FileName, string Category, string? Comment, IReadOnlyList<string> Lines);

    public sealed record DemoSnapshot(SchoolRecord School, StudentRecord Student, IReadOnlyList<GradeRecord> Grades, IReadOnlyList<ExamRecord> Exams, IReadOnlyList<AgendaRecord> Agenda, IReadOnlyList<AbsenceRecord> Absences, IReadOnlyList<SemesterReportRecord> Reports, IReadOnlyList<DemoDocument> Documents);

    /// <summary>
    /// A fictional Swiss vocational school, built from the current time on every sync. External ids and exam names
    /// never depend on the date, so each sync moves the same rows forward instead of adding new ones.
    /// </summary>
    public static class DemoSchool
    {
        public const string SchoolName = "Berufsfachschule Musterstadt";
        public const string ProgramCode = "BM1 TALS";
        public const string ClassName = "INF-BM1a";

        /// <summary>The anchor semester is the one that holds the day this many days ago, so it always has a few weeks of grades.</summary>
        public const int AnchorLagDays = 56;

        private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
        private static readonly decimal[] Weights = [1m, 1m, 2m];

        public sealed record Subject(string Code, string Name, string Teacher, string Room, decimal Level, string[] Topics);

        private sealed record Slot(DayOfWeek Day, TimeOnly Start, TimeOnly End, string Code, int Lessons);

        /// <summary>Topics 0-2 are graded in the anchor semester, 3-5 in the one before, 6 is the next exam.</summary>
        public static readonly IReadOnlyList<Subject> Subjects =
        [
            new("M", "Mathematik", "R. Beispiel", "A 204", 5.0m, ["Lineare Gleichungen", "Funktionen", "Trigonometrie", "Bruchterme", "Potenzen und Wurzeln", "Prozentrechnen", "Quadratische Gleichungen"]),
            new("D", "Deutsch", "L. Muster", "A 112", 4.8m, ["Erörterung", "Kurzgeschichte", "Grammatik", "Textanalyse", "Lyrik", "Zusammenfassung", "Literarische Epochen"]),
            new("E", "Englisch", "J. Example", "A 108", 5.4m, ["Reading Comprehension", "Essay Writing", "Vocabulary Unit 4", "Listening", "Grammar Test", "Presentation", "Short Stories"]),
            new("F", "Französisch", "C. Exemple", "A 110", 4.1m, ["Passé composé", "Compréhension écrite", "Vocabulaire 3", "Imparfait", "Expression orale", "Dictée", "Subjonctif"]),
            new("WR", "Wirtschaft und Recht", "P. Modell", "A 302", 4.9m, ["Vertragsrecht", "Marktformen", "Buchhaltung", "Konjunktur", "Kaufvertrag", "Steuern", "Arbeitsrecht"]),
            new("NW", "Naturwissenschaften", "N. Vorlage", "B 3 (Labor)", 4.6m, ["Kinematik", "Energie", "Atombau", "Chemische Bindungen", "Optik", "Elektrizität", "Säuren und Basen"]),
            new("GP", "Geschichte und Politik", "K. Probe", "A 015", 5.1m, ["Industrialisierung", "Bundesstaat 1848", "Erster Weltkrieg", "Direkte Demokratie", "Kalter Krieg", "Menschenrechte", "Globalisierung"]),
            new("INF", "Informatik", "D. Platzhalter", "C 015", 5.6m, ["Datenbanken und SQL", "Netzwerke", "Objektorientierung", "Algorithmen", "Webentwicklung", "Versionsverwaltung", "Softwaretests"]),
        ];

        private static readonly Subject Sport = new("SP", "Sport", "K. Probe", "Turnhalle", 0m, []);

        private static readonly IReadOnlyList<Slot> Timetable =
        [
            new(DayOfWeek.Monday, new(8, 0), new(9, 35), "M", 2),
            new(DayOfWeek.Monday, new(9, 55), new(11, 30), "D", 2),
            new(DayOfWeek.Monday, new(11, 35), new(12, 20), "GP", 1),
            new(DayOfWeek.Monday, new(13, 15), new(14, 50), "INF", 2),
            new(DayOfWeek.Monday, new(15, 5), new(15, 50), "WR", 1),
            new(DayOfWeek.Tuesday, new(8, 0), new(9, 35), "E", 2),
            new(DayOfWeek.Tuesday, new(9, 55), new(11, 30), "NW", 2),
            new(DayOfWeek.Tuesday, new(11, 35), new(12, 20), "M", 1),
            new(DayOfWeek.Tuesday, new(13, 15), new(14, 50), "F", 2),
            new(DayOfWeek.Thursday, new(8, 0), new(9, 35), "INF", 2),
            new(DayOfWeek.Thursday, new(9, 55), new(11, 30), "WR", 2),
            new(DayOfWeek.Thursday, new(11, 35), new(12, 20), "D", 1),
            new(DayOfWeek.Thursday, new(13, 15), new(14, 0), "SP", 1),
            new(DayOfWeek.Thursday, new(14, 5), new(14, 50), "E", 1),
            new(DayOfWeek.Thursday, new(15, 5), new(15, 50), "F", 1),
        ];

        /// <summary>Which next lesson of a subject is its next exam (1 = the next lesson).</summary>
        private static readonly IReadOnlyList<(string Code, int Occurrence)> UpcomingExams = [("M", 1), ("E", 2), ("INF", 2), ("WR", 3), ("F", 4)];

        /// <summary>The timetable covers last week, this week and the four after it.</summary>
        public const int FirstWeek = -1;
        public const int LastWeek = 4;

        public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);

        public static Semester Anchor(DateOnly today) => Semester.Of(today.AddDays(-AnchorLagDays));

        public static DemoSnapshot Build(DateTimeOffset now)
        {
            var today = Today(now);
            var anchor = Anchor(today);
            var entry = new Semester(anchor.YearStart - 1, 1);

            var school = new SchoolRecord(SchoolName);
            var student = new StudentRecord
            {
                FirstName = "Mia",
                LastName = "Muster",
                Email = "mia.muster@schule.example",
                Birthday = new DateOnly(anchor.YearStart - 17, 3, 14),
                EntryDate = entry.Start,
            };

            var grades = new List<GradeRecord>();
            var exams = new List<ExamRecord>();
            AddGraded(grades, exams, "current", anchor, 0, anchor.Start.AddDays(14), Min(anchor.End.AddDays(-14), today.AddDays(-1)));
            AddGraded(grades, exams, "previous", anchor.Previous, 3, anchor.Previous.Start.AddDays(14), anchor.Previous.End.AddDays(-14));

            var agenda = BuildAgenda(today, exams);
            var reports = new List<SemesterReportRecord>();
            for (var s = anchor.Previous; s.Start >= entry.Start; s = s.Previous)
                reports.Add(Report(s));
            reports.Reverse();

            return new DemoSnapshot(school, student, grades, exams, agenda, BuildAbsences(today), reports, BuildDocuments(today, anchor.Previous, student));
        }

        /// <summary>A subject's semester grade: the weighted exam average rounded to the nearest half, as BBB does.</summary>
        public static decimal SubjectGrade(IEnumerable<(decimal Score, decimal Weight)> exams)
        {
            var list = exams.ToList();
            var average = list.Sum(e => e.Score * e.Weight) / list.Sum(e => e.Weight);
            return Math.Round(average * 2, MidpointRounding.AwayFromZero) / 2;
        }

        /// <summary>The demo student's mark for exam <paramref name="index"/> of a subject in a semester, between 1 and 6 in tenths.</summary>
        public static decimal Score(Semester semester, Subject subject, int index)
        {
            var spread = (decimal)Noise("score", semester.YearStart, semester.Half, subject.Code, index) * 1.6m - 0.8m;
            return Math.Clamp(Math.Round(subject.Level + spread, 1, MidpointRounding.AwayFromZero), 1m, 6m);
        }

        private static void AddGraded(List<GradeRecord> grades, List<ExamRecord> exams, string block, Semester semester, int firstTopic, DateOnly from, DateOnly to)
        {
            var span = Math.Max(0, to.DayNumber - from.DayNumber);
            for (var s = 0; s < Subjects.Count; s++)
            {
                var subject = Subjects[s];
                for (var i = 0; i < Weights.Length; i++)
                {
                    var fraction = (i + 1) / 4.0 + (s - 3.5) * 0.02;
                    var date = SchoolDayOnOrBefore(from.AddDays((int)Math.Round(span * fraction)), subject.Code);
                    var name = subject.Topics[firstTopic + i];
                    var classAverage = Math.Round(4.3m + (decimal)Noise("class", semester.YearStart, semester.Half, subject.Code, i) * 0.8m, 2);
                    grades.Add(new GradeRecord(ExternalIds.Of("grade", block, subject.Code, i.ToString()), subject.Name, name, Score(semester, subject, i), Weights[i], date) { ClassAverage = classAverage });
                    // The host keeps a graded exam's first date, so the date is also sent as an exam to move it along.
                    exams.Add(new ExamRecord(subject.Name, name, date) { ClassAverage = classAverage });
                }
            }
        }

        private static List<AgendaRecord> BuildAgenda(DateOnly today, List<ExamRecord> exams)
        {
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var lessons = new List<(string Id, DateOnly Day, Slot Slot)>();
            for (var week = FirstWeek; week <= LastWeek; week++)
            {
                for (var i = 0; i < Timetable.Count; i++)
                {
                    var slot = Timetable[i];
                    var day = monday.AddDays(7 * week + ((int)slot.Day + 6) % 7);
                    lessons.Add((ExternalIds.Of("lesson", $"w{week}", slot.Day.ToString(), i.ToString()), day, slot));
                }
            }

            var tests = new Dictionary<string, Subject>();
            foreach (var (code, occurrence) in UpcomingExams)
            {
                var subject = Subjects.Single(s => s.Code == code);
                var lesson = lessons.Where(l => l.Day > today && l.Slot.Code == code).OrderBy(l => l.Day).ThenBy(l => l.Slot.Start).ElementAt(occurrence - 1);
                tests[lesson.Id] = subject;
                exams.Add(new ExamRecord(subject.Name, subject.Topics[6], lesson.Day) { Description = $"Stoff: {subject.Topics[6]}. Erlaubte Hilfsmittel gemäss Lehrperson." });
            }

            var agenda = new List<AgendaRecord>();
            foreach (var (id, day, slot) in lessons)
            {
                var subject = slot.Code == Sport.Code ? Sport : Subjects.Single(s => s.Code == slot.Code);
                var start = Swiss(day, slot.Start);
                var end = Swiss(day, slot.End);
                agenda.Add(tests.TryGetValue(id, out var tested)
                    ? new AgendaRecord(id, AgendaKind.Test, $"{tested.Name}: {tested.Topics[6]}", start, end, subject.Room, subject.Teacher)
                    : new AgendaRecord(id, AgendaKind.Lesson, subject.Name, start, end, subject.Room, subject.Teacher));
            }

            var wednesday = monday.AddDays(2);
            agenda.Add(new AgendaRecord(ExternalIds.Of("event", "infoabend"), AgendaKind.Event, "Infoabend Berufsmaturität", Swiss(wednesday.AddDays(14), new(18, 30)), Swiss(wednesday.AddDays(14), new(20, 0)), "Aula", "Für Lernende und Lehrbetriebe"));
            agenda.Add(new AgendaRecord(ExternalIds.Of("event", "sporttag"), AgendaKind.Event, "Sporttag", Swiss(wednesday.AddDays(21), new(8, 0)), Swiss(wednesday.AddDays(21), new(16, 0)), "Sportanlage Musterstadt", "Sportkleidung mitnehmen"));
            return agenda;
        }

        private static List<AbsenceRecord> BuildAbsences(DateOnly today)
        {
            AbsenceRecord FullDay(string key, int schoolDaysAgo, string reason, out DateOnly day)
            {
                day = SchoolDayBefore(today, schoolDaysAgo);
                var d = day;
                return new AbsenceRecord(ExternalIds.Of("absence", key), SwissTime.UtcDate(day), SwissTime.UtcDate(day), reason) { Lessons = Timetable.Where(s => s.Day == d.DayOfWeek).Sum(s => s.Lessons), ExcuseDeadline = day.AddDays(14) };
            }

            AbsenceRecord Partial(string key, int schoolDaysAgo, string reason, bool afternoon, out DateOnly day)
            {
                day = SchoolDayBefore(today, schoolDaysAgo);
                var d = day;
                var slots = Timetable.Where(s => s.Day == d.DayOfWeek && (s.Start.Hour >= 13) == afternoon).ToList();
                return new AbsenceRecord(ExternalIds.Of("absence", key), SwissTime.UtcDate(day), SwissTime.UtcDate(day), reason) { StartTime = slots[0].Start.ToString("HH:mm"), EndTime = slots[^1].End.ToString("HH:mm"), Lessons = slots.Sum(s => s.Lessons), ExcuseDeadline = day.AddDays(14) };
            }

            var lateDay = SchoolDayBefore(today, 5);
            var lateStart = Timetable.First(s => s.Day == lateDay.DayOfWeek).Start;
            return
            [
                // Waiting for the training company.
                Partial("arzttermin", 2, "Arzttermin", afternoon: false, out _) with { IsExcused = false, ParentStatus = "ofAge", TrainerStatus = "open", Comment = "Kontrolle beim Zahnarzt" },
                // Still to be excused, deadline ahead.
                FullDay("krank-offen", 4, "Krankheit", out _) with { IsExcused = false },
                new AbsenceRecord(ExternalIds.Of("late", "zug"), Swiss(lateDay, lateStart), Swiss(lateDay, lateStart.AddMinutes(10)), "Zug verspätet", IsLate: true) { IsExcused = true, Lessons = 1 },
                FullDay("krank-entschuldigt", 8, "Krankheit", out var sickDay) with { IsExcused = true, ParentStatus = "ofAge", TrainerStatus = "confirmed", TrainerStatusDate = sickDay.AddDays(2), TrainerComment = "Gute Besserung!" },
                // Confirmed by the training company, waiting for the school.
                Partial("familie", 11, "Familiäre Gründe", afternoon: true, out var familyDay) with { IsExcused = false, ParentStatus = "ofAge", TrainerStatus = "confirmed", TrainerStatusDate = familyDay.AddDays(3) },
                // Deadline missed.
                FullDay("unentschuldigt", 16, "Krankheit", out _) with { IsExcused = false },
                FullDay("rekrutierung", 22, "Militärische Rekrutierung", out _) with { IsExcused = true, Comment = "Aufgebot abgegeben" },
            ];
        }

        private static SemesterReportRecord Report(Semester semester)
        {
            var subjects = Subjects.Select(s => new SemesterSubjectRecord(s.Code, s.Name, SubjectGrade(Weights.Select((w, i) => (Score(semester, s, i), w))))).ToList();
            var average = Math.Round(subjects.Average(s => s.Grade!.Value), 1, MidpointRounding.AwayFromZero);
            var excused = 2 + (int)(Noise("excused", semester.YearStart, semester.Half) * 10);
            var unexcused = Noise("unexcused", semester.YearStart, semester.Half) < 0.3 ? 1 : 0;
            return new SemesterReportRecord(ProgramCode, semester.YearStart, semester.Half, ClassName, subjects, "promoviert", average, excused, unexcused);
        }

        private static List<DemoDocument> BuildDocuments(DateOnly today, Semester last, StudentRecord student)
        {
            var report = Report(last);
            var name = $"{student.FirstName} {student.LastName}";
            var reportLines = new List<string> { $"{SchoolName} - {ProgramCode}", $"Lernende: {name}, Klasse {ClassName}", "" };
            reportLines.AddRange(report.Subjects.Select(s => $"{s.SubjectName}: {s.Grade:0.0}"));
            reportLines.AddRange(["", $"Durchschnitt: {report.GradeAverage:0.0}", $"Promotion: {report.PromotionDecision}", $"Absenzen entschuldigt: {report.ExcusedAbsences}, unentschuldigt: {report.UnexcusedAbsences}"]);

            return
            [
                new DemoDocument(ExternalIds.Of("doc", "zeugnis", $"{last.YearStart}-{last.Half}"), $"Semesterzeugnis {last.Label}", $"Semesterzeugnis_{last.YearStart % 100:00}-{(last.YearStart + 1) % 100:00}_{last.Half}.pdf", "Zeugnisse", null, reportLines),
                new DemoDocument(ExternalIds.Of("doc", "schulbestaetigung"), "Schulbestätigung", "Schulbestaetigung.pdf", "Bestätigungen", "Für den Lehrbetrieb", [SchoolName, "", $"Hiermit bestätigen wir, dass {name} die Berufsmaturitätsschule", $"in der Klasse {ClassName} besucht.", "", $"Musterstadt, {today:dd.MM.yyyy}"]),
                new DemoDocument(ExternalIds.Of("doc", "hausordnung"), "Hausordnung", "Hausordnung.pdf", "Allgemein", null, [SchoolName, "", "1. Der Unterricht beginnt pünktlich.", "2. Absenzen sind innert 14 Tagen zu entschuldigen.", "3. Mobiltelefone bleiben im Unterricht lautlos."]),
            ];
        }

        /// <summary>The nth school day (a day with lessons) before <paramref name="today"/>.</summary>
        public static DateOnly SchoolDayBefore(DateOnly today, int n)
        {
            var day = today;
            for (var found = 0; found < n;)
            {
                day = day.AddDays(-1);
                if (IsSchoolDay(day, null)) found++;
            }
            return day;
        }

        private static DateOnly SchoolDayOnOrBefore(DateOnly day, string code)
        {
            while (!IsSchoolDay(day, code))
                day = day.AddDays(-1);
            return day;
        }

        private static bool IsSchoolDay(DateOnly day, string? code) => Timetable.Any(s => s.Day == day.DayOfWeek && (code is null || s.Code == code));

        private static DateTimeOffset Swiss(DateOnly day, TimeOnly time) =>
            SwissTime.TryToUtc(day.ToDateTime(time), out var utc) ? utc : throw new InvalidOperationException($"{day} {time} does not exist in Swiss time.");

        private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

        /// <summary>A stable number in [0, 1) for the given parts (FNV-1a plus a MurmurHash3 finalizer), so the same inputs always give the same data.</summary>
        private static double Noise(params object[] parts)
        {
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(string.Join('|', parts)))
                hash = (hash ^ b) * 16777619u;
            hash ^= hash >> 16;
            hash *= 0x85ebca6bu;
            hash ^= hash >> 13;
            hash *= 0xc2b2ae35u;
            hash ^= hash >> 16;
            return hash / (uint.MaxValue + 1.0);
        }
    }
}
