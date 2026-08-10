using System.Collections.Generic;

namespace WorkLedger_Client.Sync
{
    // Outcome of a single PushAsync call.
    public class SyncResult
    {
        // SyncQueue.Id values the provider actually accepted; SyncOrchestrator marks
        // exactly these rows IsSynced = true. Anything left out (whether omitted outright
        // or reported in Failures) stays unsynced and is retried on the next push.
        public List<int> AcceptedSyncQueueIds { get; init; } = new();

        // Per-row failure detail for entries that were rejected rather than merely
        // omitted. Not acted on yet (no retry/backoff policy exists), but the shape is
        // here so a future policy has something to key off of.
        public List<SyncFailure> Failures { get; init; } = new();
    }

    public class SyncFailure
    {
        public int SyncQueueId { get; init; }

        public string? ErrorMessage { get; init; }
    }
}
