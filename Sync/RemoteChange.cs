using System;

namespace WorkLedger_Client.Sync
{
    // A change pulled from a remote source, mirroring SyncQueue's shape since it
    // describes the same kind of event from a different origin. Version and
    // OriginDeviceId are surfaced directly (rather than left buried in PayloadJson) so
    // SyncOrchestrator can run last-write-wins comparisons without deserializing the
    // payload per entity type just to make that decision.
    public class RemoteChange
    {
        public string EntityType { get; set; } = string.Empty;

        // The affected entity's SyncId - see WL-47/WL-54. Never the local autoincrement
        // Id, which two independently-offline devices can collide on.
        public Guid EntityId { get; set; }

        public string Operation { get; set; } = string.Empty;

        public string? PayloadJson { get; set; }

        public int Version { get; set; }

        public string? OriginDeviceId { get; set; }
    }
}
