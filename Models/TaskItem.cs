using SQLite;
using System;

namespace WorkLedger_Client.Models
{
    public class TaskItem
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int ProjectId { get; set; }

        public string? Name { get; set; }

        public string? Notes { get; set; }

        public bool IsCompleted { get; set; }

        public bool IsArchived { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }
    }
}
