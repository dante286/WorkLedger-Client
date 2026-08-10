using System;

namespace WorkLedger_Client.Models
{
    public interface IVersionedEntity
    {
        int Version { get; set; }
        string? OriginDeviceId { get; set; }

        // Stable cross-device identity, generated once at creation and never reassigned.
        // The local [PrimaryKey, AutoIncrement] Id is only unique within a single device's
        // database - two offline devices can independently land the same entity type on
        // the same local Id, so sync (SyncQueue, RemoteChange, orchestrator merge lookups)
        // must key off SyncId instead. See WL-47.
        Guid SyncId { get; set; }
    }
}
