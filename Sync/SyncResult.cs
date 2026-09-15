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

        // WL-101: the winning rows the remote handed back when it rejected a change because
        // its own copy won. The orchestrator merges these through exactly the path a pulled
        // change takes, so a conflict costs one merge rather than a re-pull.
        //
        // Adopting them is only safe because the remote states what each payload *is*: the
        // payload DTOs carry no IsDeleted, so a payload on its own can't be told apart from a
        // tombstone's, and synthesizing an "update" from a tombstone resurrects a deleted row
        // locally and permanently (reachable case - a third device deleted the entity at v5
        // while we push an update at v4). SyncItemResult.AuthoritativeOperation (WL-98) is what
        // removes the guess; a winner that arrives without one falls back to RequiresFullPull.
        //
        // These are merges, not local edits, so they keep the author's Version and
        // OriginDeviceId and enqueue nothing - see SyncOrchestrator.WriteMergedAsync and WL-99.
        public List<RemoteChange> AuthoritativeChanges { get; init; } = new();

        // The fallback for a server-wins rejection that AuthoritativeChanges couldn't carry:
        // the remote sent a winning payload but didn't say whether it's a live row or a
        // tombstone (a server predating WL-98, or an operation this client doesn't recognize).
        // The orchestrator answers by rewinding its pull cursor, because a pull always states
        // the operation explicitly.
        //
        // Correct but expensive - it re-pulls the account's whole history, and the version
        // merge then discards everything already held - which is why the self-describing case
        // above is now the normal path.
        //
        // A plain *incremental* re-pull isn't enough on its own: a losing item performs no write
        // server-side (deliberately - a write would restamp UpdatedAt and re-broadcast the
        // row to every device), so the winner's UpdatedAt is already behind our cursor and
        // an ordinary incremental pull would never re-deliver it.
        public bool RequiresFullPull { get; init; }

        // Per-row failure detail. Carries the reason for both retryable transport faults
        // and the permanent rejections listed above; RejectedSyncQueueIds is what tells the
        // two apart.
        public List<SyncFailure> Failures { get; init; } = new();

        // WL-134: this push got nowhere because there is no usable session - no token, an
        // expired one, or one the server has revoked. Threaded out through SyncRunResult so the
        // UI can say "you've been signed out" instead of "sync failed: 401", which is the
        // difference between a message someone can act on and one they file a bug about.
        //
        // The rows involved are a TRANSPORT outcome, not a rejection: they belong in Failures
        // and stay pending, because the server never saw them. Retiring them would discard work
        // on the strength of a signed-out session.
        public bool AuthenticationRequired { get; init; }
    }

    public class SyncFailure
    {
        public int SyncQueueId { get; init; }

        public string? ErrorMessage { get; init; }
    }
}
