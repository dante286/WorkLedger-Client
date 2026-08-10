using SQLite;
using System;

namespace WorkLedger_Client.Models
{
    public class Project : IVersionedEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public string? Name { get; set; }

        public string? ProjectCode { get; set; }

        public string? Description { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        public int Version { get; set; } = 1;

        public string? OriginDeviceId { get; set; }

        [Indexed]
        public Guid SyncId { get; set; } = Guid.NewGuid();
    }
}
