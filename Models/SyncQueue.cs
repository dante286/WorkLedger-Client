using SQLite;
using System;

namespace WorkLedger_Client.Models
{
    public class SyncQueue
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        // e.g., "project", "task", "timeentry"
        [Indexed]
        public string? EntityType { get; set; }

        // The affected entity's SyncId (Project/TaskItem/TimeEntry.SyncId) - NOT the local
        // autoincrement Id, which only has meaning within this device's own database. See WL-47/WL-54.
        public Guid EntityId { get; set; }

        // e.g., "create", "update", "delete"
        public string? Operation { get; set; }

        // JSON payload of the entity at the time of the change
        public string? PayloadJson { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public bool IsSynced { get; set; } = false;

    }
}
