using SQLite;
using System;

namespace WorkLedger_Client.Models
{
    public class TimeEntry : IVersionedEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        [Indexed]
        public int TaskItemId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public long? DurationSeconds { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        public int Version { get; set; } = 1;
        public string? OriginDeviceId { get; set; }

        [Indexed]
        public Guid SyncId { get; set; } = Guid.NewGuid();

        [Ignore]
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : DateTime.UtcNow - StartTime;
    }
}
