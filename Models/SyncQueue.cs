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

        // Local entity ID (e.g., Project.Id)
        public int EntityId { get; set; }

        // e.g., "create", "update", "delete"
        public string? Operation { get; set; }

        // JSON payload of the entity at the time of the change
        public string? PayloadJson { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public bool IsSynced { get; set; } = false;

    }
}
