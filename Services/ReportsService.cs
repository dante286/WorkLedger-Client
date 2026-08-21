using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Services
{
    // Local, read-only weekly rollups for the Reports tab (plan 7 items 7-8).
    //
    // Deliberately client-side: WorkLedger-API has its own /reports/* endpoints, but this
    // view has to work with sync switched off entirely, so it is computed from
    // LocalDbService and nothing else. The two are meant to agree, which is what makes
    // StartOfIsoWeek below a correctness fix rather than a preference.
    public class ReportsService
    {
        private readonly LocalDbService _db;

        public ReportsService(LocalDbService db) => _db = db;

        /// <summary>
        /// Monday 00:00 UTC of the ISO-8601 week containing <paramref name="utcDate"/>.
        /// </summary>
        // WL-102. The single definition of "this week" in this codebase - the header total in
        // MainWindowViewModel and the Reports tab both call it, so they cannot drift apart.
        //
        // It matches the server's ReportRange (WorkLedger-API,
        // src/WorkLedger.Server/Reports/ReportRange.cs), which starts the week on Monday.
        // The client used to subtract (int)DayOfWeek, which numbers Sunday as 0 and therefore
        // started a *new* week every Sunday - so on Sundays the client and the server reported
        // different totals for the same account. The +6 %7 shift maps Monday to 0 and Sunday
        // to 6, which is the ISO ordering.
        public static DateTime StartOfIsoWeek(DateTime utcDate)
            => utcDate.Date.AddDays(-(((int)utcDate.DayOfWeek + 6) % 7));

        /// <summary>
        /// Per-project and per-task totals for the ISO week containing <paramref name="nowUtc"/>.
        /// </summary>
        public async Task<WeeklyReport> BuildWeeklyReportAsync(DateTime nowUtc)
        {
            // Three table reads rather than the per-task query loop the Projects tab does:
            // a report spans every project, so N+1 queries here would be N+1 over the whole
            // database rather than over one project's tasks.
            var projects = await _db.GetAllAsync<Project>();
            var tasks = await _db.GetAllAsync<TaskItem>();
            var entries = await _db.GetAllAsync<TimeEntry>();

            return Build(projects, tasks, entries, nowUtc);
        }

        // Split out from the query so the aggregation can be tested against hand-built rows
        // without a database, and so the week banding lives in exactly one place.
        internal static WeeklyReport Build(
            IEnumerable<Project> projects,
            IEnumerable<TaskItem> tasks,
            IEnumerable<TimeEntry> entries,
            DateTime nowUtc)
        {
            var weekStart = StartOfIsoWeek(nowUtc);
            var weekEnd = weekStart.AddDays(7);

            // Banded by StartTime, and running entries (no EndTime) are excluded - both rules
            // match the server's, so an entry lands in exactly one week on both sides.
            var secondsByTaskId = entries
                .Where(entry => entry.EndTime != null && entry.StartTime >= weekStart && entry.StartTime < weekEnd)
                .GroupBy(entry => entry.TaskItemId)
                .ToDictionary(group => group.Key, group => group.Sum(entry => entry.DurationSeconds ?? 0));

            var tasksByProjectId = tasks.GroupBy(task => task.ProjectId).ToDictionary(g => g.Key, g => g.ToList());

            var projectRows = new List<ProjectReportRow>();
            foreach (var project in projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!tasksByProjectId.TryGetValue(project.Id, out var projectTasks))
                    continue;

                var taskRows = projectTasks
                    .Select(task => new TaskReportRow
                    {
                        TaskName = task.Name ?? string.Empty,
                        TotalSeconds = secondsByTaskId.TryGetValue(task.Id, out var seconds) ? seconds : 0,
                        IsArchived = task.IsArchived
                    })
                    // Zero-time rows are dropped rather than listed: a report of everything
                    // that wasn't worked on this week is noise, and the empty case gets its
                    // own message in the UI. Archived tasks are kept when they do carry time -
                    // the hours were still spent.
                    .Where(row => row.TotalSeconds > 0)
                    .OrderByDescending(row => row.TotalSeconds)
                    .ThenBy(row => row.TaskName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                if (taskRows.Count == 0)
                    continue;

                projectRows.Add(new ProjectReportRow
                {
                    ProjectName = project.Name ?? string.Empty,
                    TotalSeconds = taskRows.Sum(row => row.TotalSeconds),
                    Tasks = taskRows
                });
            }

            return new WeeklyReport
            {
                WeekStartUtc = weekStart,
                Projects = projectRows,
                TotalSeconds = projectRows.Sum(row => row.TotalSeconds)
            };
        }

        /// <summary>
        /// RFC 4180 CSV of both levels of the report: one row per project, one per task, and a
        /// grand total. The Scope column says which is which.
        /// </summary>
        // WL-50. Both breakdowns go into one file rather than one export per view: they are
        // computed from the same pass and a reader wanting only the project rollup can filter
        // on Scope, whereas splitting them makes reconciling the two files the reader's job.
        public static string ToCsv(WeeklyReport report)
        {
            var csv = new StringBuilder();
            csv.Append("Week Start (UTC),Week End (UTC),Scope,Project,Task,Duration,Seconds\r\n");

            var weekStart = report.WeekStartUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var weekEnd = report.WeekLastDayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            foreach (var project in report.Projects)
            {
                AppendRow(csv, weekStart, weekEnd, "Project", project.ProjectName, string.Empty,
                    project.Duration, project.TotalSeconds);

                foreach (var task in project.Tasks)
                {
                    AppendRow(csv, weekStart, weekEnd, "Task", project.ProjectName, task.TaskName,
                        task.Duration, task.TotalSeconds);
                }
            }

            AppendRow(csv, weekStart, weekEnd, "Total", string.Empty, string.Empty,
                report.TotalDuration, report.TotalSeconds);

            return csv.ToString();
        }

        private static void AppendRow(
            StringBuilder csv, string weekStart, string weekEnd, string scope,
            string project, string task, string duration, long seconds)
        {
            csv.Append(weekStart).Append(',')
               .Append(weekEnd).Append(',')
               .Append(scope).Append(',')
               .Append(Escape(project)).Append(',')
               .Append(Escape(task)).Append(',')
               .Append(duration).Append(',')
               .Append(seconds.ToString(CultureInfo.InvariantCulture))
               .Append("\r\n");
        }

        // Project and task names are free text, so a comma, a quote, or a pasted newline in
        // one of them would otherwise shift every following column.
        private static string Escape(string field)
        {
            if (!field.Contains(',') && !field.Contains('"') && !field.Contains('\n') && !field.Contains('\r'))
                return field;

            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
    }
}
