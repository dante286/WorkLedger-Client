using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using Xunit;

namespace WorkLedger.Tests
{
    // Plan 7 items 7-9. The week boundary is the half most likely to regress, so it gets its
    // own cases: the old expression started the week on Sunday and disagreed with the server's
    // ISO week for a full day out of every seven (WL-102).
    public class ReportsServiceTests
    {
        // 2026-08-17 is a Monday, so this is the reference week throughout: Monday 2026-08-17
        // 00:00 UTC through Sunday 2026-08-23 23:59:59 UTC.
        private static readonly DateTime MondayUtc = new(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void StartOfIsoWeek_OnMonday_ResolvesToThatSameDay()
        {
            var midMonday = MondayUtc.AddHours(14).AddMinutes(37);

            Assert.Equal(MondayUtc, ReportsService.StartOfIsoWeek(midMonday));
        }

        [Fact]
        public void StartOfIsoWeek_OnSunday_ResolvesBackToThePrecedingMonday()
        {
            // The case the old Sunday-start expression got wrong: it treated this as the first
            // day of a new week, a day before the server did.
            var sunday = MondayUtc.AddDays(6).AddHours(23).AddMinutes(59);

            Assert.Equal(MondayUtc, ReportsService.StartOfIsoWeek(sunday));
            Assert.Equal(DayOfWeek.Monday, ReportsService.StartOfIsoWeek(sunday).DayOfWeek);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void StartOfIsoWeek_EveryDayOfTheWeek_ResolvesToTheSameMonday(int dayOffset)
        {
            var day = MondayUtc.AddDays(dayOffset).AddHours(9);

            Assert.Equal(MondayUtc, ReportsService.StartOfIsoWeek(day));
        }

        [Fact]
        public void Build_EntryStartedSundayNight_CountsInTheWeekThatBeganSixDaysEarlier()
        {
            // Sunday 23:59 UTC belongs to the week that started the previous Monday - and is
            // still there when "now" is that same Sunday.
            var sundayNight = MondayUtc.AddDays(6).AddHours(23).AddMinutes(59);
            var report = Build(nowUtc: sundayNight, entries: new[] { Entry(taskId: 1, start: sundayNight, seconds: 600) });

            Assert.Equal(MondayUtc, report.WeekStartUtc);
            Assert.Equal(600, report.TotalSeconds);
        }

        [Fact]
        public void Build_EntryFromTheWeekBefore_IsExcluded()
        {
            var lastSunday = MondayUtc.AddMinutes(-1);
            var report = Build(nowUtc: MondayUtc.AddDays(2), entries: new[] { Entry(taskId: 1, start: lastSunday, seconds: 3600) });

            Assert.True(report.IsEmpty);
            Assert.Equal(0, report.TotalSeconds);
        }

        [Fact]
        public void Build_EntryFromTheFollowingWeek_IsExcluded()
        {
            // The upper bound matters as much as the lower one: without it a report run
            // mid-week would pick up entries dated into next week.
            var nextMonday = MondayUtc.AddDays(7);
            var report = Build(nowUtc: MondayUtc.AddDays(2), entries: new[] { Entry(taskId: 1, start: nextMonday, seconds: 3600) });

            Assert.True(report.IsEmpty);
        }

        [Fact]
        public void Build_RunningEntry_IsExcludedFromTotals()
        {
            // No EndTime means the timer is still going. The server excludes these too, so
            // counting them here would make the two disagree on every open timer.
            var running = new TimeEntry { TaskItemId = 1, StartTime = MondayUtc.AddHours(9), EndTime = null, DurationSeconds = null };
            var report = Build(nowUtc: MondayUtc.AddHours(10), entries: new[] { running });

            Assert.True(report.IsEmpty);
        }

        [Fact]
        public void Build_SumsPerTaskAndRollsUpPerProject()
        {
            var report = Build(
                nowUtc: MondayUtc.AddDays(3),
                projects: new[] { Project(1, "Alpha"), Project(2, "Beta") },
                tasks: new[] { Task(1, 1, "Write docs"), Task(2, 1, "Review"), Task(3, 2, "Ship it") },
                entries: new[]
                {
                    Entry(taskId: 1, start: MondayUtc.AddHours(9), seconds: 3600),
                    Entry(taskId: 1, start: MondayUtc.AddDays(1).AddHours(9), seconds: 1800),
                    Entry(taskId: 2, start: MondayUtc.AddDays(2).AddHours(9), seconds: 900),
                    Entry(taskId: 3, start: MondayUtc.AddDays(2).AddHours(14), seconds: 7200)
                });

            Assert.Equal(13500, report.TotalSeconds);

            var alpha = report.Projects.Single(p => p.ProjectName == "Alpha");
            Assert.Equal(6300, alpha.TotalSeconds);

            // Tasks are ordered by time spent, so the biggest contributor reads first.
            Assert.Equal(new[] { "Write docs", "Review" }, alpha.Tasks.Select(t => t.TaskName).ToArray());
            Assert.Equal(5400, alpha.Tasks.Single(t => t.TaskName == "Write docs").TotalSeconds);
            Assert.Equal(900, alpha.Tasks.Single(t => t.TaskName == "Review").TotalSeconds);

            var beta = report.Projects.Single(p => p.ProjectName == "Beta");
            Assert.Equal(7200, beta.TotalSeconds);
        }

        [Fact]
        public void Build_ProjectsAndTasksWithNoTimeThisWeek_AreOmitted()
        {
            var report = Build(
                nowUtc: MondayUtc.AddDays(1),
                projects: new[] { Project(1, "Alpha"), Project(2, "Dormant") },
                tasks: new[] { Task(1, 1, "Worked on"), Task(2, 1, "Untouched"), Task(3, 2, "Also untouched") },
                entries: new[] { Entry(taskId: 1, start: MondayUtc.AddHours(9), seconds: 60) });

            var project = Assert.Single(report.Projects);
            Assert.Equal("Alpha", project.ProjectName);
            var task = Assert.Single(project.Tasks);
            Assert.Equal("Worked on", task.TaskName);
        }

        [Fact]
        public void Build_ArchivedTaskWithTimeThisWeek_IsStillCounted()
        {
            // Archiving hides a task from the Projects tab; it doesn't unspend the hours.
            var archived = Task(1, 1, "Archived work");
            archived.IsArchived = true;

            var report = Build(
                nowUtc: MondayUtc.AddDays(1),
                tasks: new[] { archived },
                entries: new[] { Entry(taskId: 1, start: MondayUtc.AddHours(9), seconds: 120) });

            var task = Assert.Single(report.Projects.Single().Tasks);
            Assert.True(task.IsArchived);
            Assert.Equal(120, task.TotalSeconds);
        }

        [Fact]
        public void ReportDuration_TotalOverTwentyFourHours_DoesNotWrap()
        {
            // TimeSpan's "hh" specifier would render this as 17:00:00.
            Assert.Equal("41:00:00", ReportDuration.Format(41 * 3600));
        }

        [Fact]
        public void ToCsv_WritesProjectRowsTaskRowsAndAGrandTotal()
        {
            var report = Build(
                nowUtc: MondayUtc.AddDays(1),
                projects: new[] { Project(1, "Alpha") },
                tasks: new[] { Task(1, 1, "Write docs") },
                entries: new[] { Entry(taskId: 1, start: MondayUtc.AddHours(9), seconds: 5400) });

            var lines = ReportsService.ToCsv(report).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal("Week Start (UTC),Week End (UTC),Scope,Project,Task,Duration,Seconds", lines[0]);
            Assert.Equal("2026-08-17,2026-08-23,Project,Alpha,,01:30:00,5400", lines[1]);
            Assert.Equal("2026-08-17,2026-08-23,Task,Alpha,Write docs,01:30:00,5400", lines[2]);
            Assert.Equal("2026-08-17,2026-08-23,Total,,,01:30:00,5400", lines[3]);
            Assert.Equal(4, lines.Length);
        }

        [Fact]
        public void ToCsv_NamesContainingCommasAndQuotes_AreQuoted()
        {
            // Free-text names would otherwise shift every column to their right.
            var report = Build(
                nowUtc: MondayUtc.AddDays(1),
                projects: new[] { Project(1, "Acme, Inc.") },
                tasks: new[] { Task(1, 1, "Say \"hello\"") },
                entries: new[] { Entry(taskId: 1, start: MondayUtc.AddHours(9), seconds: 60) });

            var csv = ReportsService.ToCsv(report);

            Assert.Contains("\"Acme, Inc.\"", csv);
            Assert.Contains("\"Say \"\"hello\"\"\"", csv);
        }

        [Fact]
        public async Task BuildWeeklyReportAsync_ReadsThroughLocalDbService()
        {
            // The aggregation is covered above against hand-built rows; this covers the query
            // path - that the three table reads line up with what the merge/save paths write.
            var dbPath = Path.Combine(Path.GetTempPath(), $"WorkLedgerReportTests_{Guid.NewGuid()}.db");
            var db = new LocalDbService(dbPath, "report-device");
            try
            {
                await db.Initialization;

                var project = new Project { Name = "Alpha" };
                await db.SaveProjectAsync(project);

                var task = new TaskItem { ProjectId = project.Id, Name = "Write docs" };
                await db.SaveTaskAsync(task);

                var now = DateTime.UtcNow;
                var weekStart = ReportsService.StartOfIsoWeek(now);
                await db.SaveTimeEntryAsync(new TimeEntry
                {
                    TaskItemId = task.Id,
                    StartTime = weekStart.AddHours(9),
                    EndTime = weekStart.AddHours(10),
                    DurationSeconds = 3600
                });

                var report = await new ReportsService(db).BuildWeeklyReportAsync(now);

                Assert.Equal(3600, report.TotalSeconds);
                Assert.Equal("Alpha", report.Projects.Single().ProjectName);
                Assert.Equal("Write docs", report.Projects.Single().Tasks.Single().TaskName);
            }
            finally
            {
                await db.Connection.CloseAsync();
                TryDeleteWithRetries(dbPath);
            }
        }

        private static WeeklyReport Build(
            DateTime nowUtc,
            IEnumerable<Project>? projects = null,
            IEnumerable<TaskItem>? tasks = null,
            IEnumerable<TimeEntry>? entries = null)
            => ReportsService.Build(
                projects ?? new[] { Project(1, "Alpha") },
                tasks ?? new[] { Task(1, 1, "Write docs") },
                entries ?? Array.Empty<TimeEntry>(),
                nowUtc);

        private static Project Project(int id, string name) => new() { Id = id, Name = name };

        private static TaskItem Task(int id, int projectId, string name)
            => new() { Id = id, ProjectId = projectId, Name = name };

        private static TimeEntry Entry(int taskId, DateTime start, long seconds) => new()
        {
            TaskItemId = taskId,
            StartTime = start,
            EndTime = start.AddSeconds(seconds),
            DurationSeconds = seconds
        };

        private static void TryDeleteWithRetries(string path, int maxAttempts = 5)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
    }
}
