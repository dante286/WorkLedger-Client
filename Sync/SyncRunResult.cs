using System;

namespace WorkLedger_Client.Sync
{
    // What one SyncOrchestrator.RunSyncAsync() call did, for a caller that has to say
    // something about it - the Settings tab's "Sync now" button, mainly. The orchestrator
    // itself treats a failed push or pull as "try again next tick" and keeps going, so
    // without this a manual sync would report success no matter what happened.
    //
    // Ran and Succeeded are deliberately separate: a run that never started because another
    // one was still in flight has not failed, and telling the user it did would be wrong.
    public class SyncRunResult
    {
        // False only when a sync was already running and this call declined to start a
        // second one. See SyncOrchestrator's run lock.
        public bool Ran { get; init; }

        // True when the push reported no transport failures and the pull came back
        // successfully. Rejected pushes don't count against this: a rejection is the
        // server's considered answer, not a failure to reach it.
        public bool Succeeded { get; init; }

        public string? ErrorMessage { get; init; }

        // Queue rows the provider accepted or rejected outright - i.e. rows this run
        // settled and will not send again.
        public int PushedCount { get; init; }

        // Remote changes the pull delivered. Not all of them necessarily won their version
        // comparison, so this counts what arrived, not what was written.
        public int ReceivedCount { get; init; }

        public static SyncRunResult Skipped() => new() { Ran = false, Succeeded = true };
    }
}
