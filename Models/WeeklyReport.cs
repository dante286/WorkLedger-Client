using System;
using System.Collections.Generic;

namespace WorkLedger_Client.Models
{
    // Read-only projections behind the Reports tab. Nothing here is persisted - every value
    // is derived from Project/TaskItem/TimeEntry rows by ReportsService, so there is no
    // schema change and no second source of truth for a total.
    public class WeeklyReport
    {
        // Monday 00:00 UTC. The week runs [WeekStartUtc, WeekEndExclusiveUtc), which is the
        // same half-open band the server's /reports/weekly uses - see ReportsService.StartOfIsoWeek.
        public DateTime WeekStartUtc { get; init; }

        public DateTime WeekEndExclusiveUtc => WeekStartUtc.AddDays(7);

        // The last day *in* the week, for display. The exclusive end above is the one to
        // compare against.
        public DateTime WeekLastDayUtc => WeekStartUtc.AddDays(6);

        public IReadOnlyList<ProjectReportRow> Projects { get; init; } = Array.Empty<ProjectReportRow>();

        public long TotalSeconds { get; init; }

        public string TotalDuration => ReportDuration.Format(TotalSeconds);

        public bool IsEmpty => Projects.Count == 0;

        public string WeekRangeLabel => $"{WeekStartUtc:ddd d MMM yyyy} – {WeekLastDayUtc:ddd d MMM yyyy} (UTC)";
    }

    public class ProjectReportRow
    {
        public string ProjectName { get; init; } = string.Empty;

        public long TotalSeconds { get; init; }

        public string Duration => ReportDuration.Format(TotalSeconds);

        public IReadOnlyList<TaskReportRow> Tasks { get; init; } = Array.Empty<TaskReportRow>();
    }

    public class TaskReportRow
    {
        public string TaskName { get; init; } = string.Empty;

        public long TotalSeconds { get; init; }

        public string Duration => ReportDuration.Format(TotalSeconds);

        public bool IsArchived { get; init; }
    }

    // A weekly total routinely passes 24 hours, which is exactly where TimeSpan's "hh"
    // format specifier silently wraps - SecondsToDurationConverter has the same limit and is
    // fine for a single entry. Total hours are carried into the hour field instead.
    public static class ReportDuration
    {
        public static string Format(long totalSeconds)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
            return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
        }
    }
}
