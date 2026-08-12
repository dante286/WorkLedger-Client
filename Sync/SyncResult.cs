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

        // WL-94: SyncQueue.Id values the remote refused *permanently* - a validation
        // failure, or a version the server's own row won. Neither changes on a re-push, so
        // these are retired alongside the accepted ones rather than left pending. Without
        // this they'd sit in the queue and be re-pushed on every sync forever.
        //
        // Deliberately separate from Failures: a Failures entry can be a transient IO or
        // network fault, which is exactly the case that *must* be retried. "The remote said
        // no" and "the remote couldn't be reached" are opposite instructions.
        public List<int> RejectedSyncQueueIds { get; init; } = new();

        // Set when the remote rejected a change because its own row won, rather than
        // because the payload was invalid. The orchestrator answers by rewinding its pull
        // cursor so the next pull re-delivers the remote's authoritative state.
        //
        // That indirection is not laziness - it's the only safe option. The server hands
        // back the winning payload on rejection, but SyncItemResult has no field saying
        // whether the winning row is a *tombstone*, and the payload DTOs carry no IsDeleted
        // either. Reachable case: a third device deleted the entity (tombstone at v5) while
        // we push an update at v4. Synthesizing an "update" from that payload would
        // resurrect a deleted row, locally and permanently. A pull states the operation
        // explicitly ("delete" when IsDeleted), so asking for one gets the truth instead of
        // a guess. See WL-98.
        //
        // A plain re-pull isn't enough on its own: a losing item performs no write
        // server-side (deliberately - a write would restamp UpdatedAt and re-broadcast the
        // row to every device), so the winner's UpdatedAt is already behind our cursor and
        // an ordinary incremental pull would never re-deliver it.
        public bool RequiresFullPull { get; init; }

        // Per-row failure detail. Carries the reason for both retryable transport faults
        // and the permanent rejections listed above; RejectedSyncQueueIds is what tells the
        // two apart.
        public List<SyncFailure> Failures { get; init; } = new();
    }

    public class SyncFailure
    {
        public int SyncQueueId { get; init; }

        public string? ErrorMessage { get; init; }
    }
}
