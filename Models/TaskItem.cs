using SQLite;
using System;

namespace WorkLedger_Client.Models
{
    public class TaskItem : IVersionedEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int ProjectId { get; set; }

        // WL-91: the cross-device half of the parent link. ProjectId above is a local
        // autoincrement value with no meaning on another device, so it is the one field
        // of a synced payload that must never be trusted as received - the orchestrator
        // resolves this SyncId back to whatever local Id the parent happens to have here.
        // Deliberately no Guid.NewGuid() initializer, unlike SyncId: this is a foreign
        // key, and Guid.Empty is the meaningful "parent unknown" sentinel that both the
        // backfill and the orphan re-link pass key off of.
        [Indexed]
        public Guid ProjectSyncId { get; set; }

        public string? Name { get; set; }

        public string? Notes { get; set; }

        public bool IsCompleted { get; set; }

        public bool IsArchived { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        public int Version { get; set; } = 1;

        public string? OriginDeviceId { get; set; }

        [Indexed]
        public Guid SyncId { get; set; } = Guid.NewGuid();
    }
}
