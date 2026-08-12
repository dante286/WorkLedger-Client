using System;
using System.Collections.Generic;

namespace WorkLedger_Client.Sync
{
    // Outcome of a single PullAsync call.
    //
    // WL-92: PullAsync used to return a bare change list, which left the orchestrator no
    // choice but to advance its cursor with its own DateTime.UtcNow. That is the wrong
    // clock - a device running fast asks for changes since a moment the server hasn't
    // reached and silently misses the window; a slow one re-pulls. ServerTime carries back
    // whatever the remote considers "now" so the next `since` is the remote's, not ours.
    public class PullResult
    {
        public IReadOnlyList<RemoteChange> Changes { get; init; } = Array.Empty<RemoteChange>();

        // What the next PullAsync should pass as `since`. Server-authoritative for
        // ApiSyncProvider; FileSyncProvider reports its own clock, since there is no server
        // in that topology and the abstraction shouldn't pretend otherwise.
        public DateTime ServerTime { get; init; }

        // False when the pull didn't complete (unreachable host, non-2xx, malformed body).
        // The distinction matters beyond reporting: the cursor must not advance past changes
        // that were never delivered, so a failed pull has to be told apart from an empty one.
        public bool Succeeded { get; init; } = true;

        public string? ErrorMessage { get; init; }

        public static PullResult Failed(string errorMessage) => new()
        {
            Succeeded = false,
            ErrorMessage = errorMessage
        };
    }
}
