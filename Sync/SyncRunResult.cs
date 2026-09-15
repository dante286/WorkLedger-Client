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

        // WL-134: this run needs a human to sign in before it can work. Deliberately one bool
        // and not a richer failure taxonomy - there is one caller and one thing it does with
        // this, which is route the user to the sign-in form instead of retrying forever against
        // a token that will never work again.
        public bool AuthenticationRequired { get; init; }

        public static SyncRunResult Skipped() => new() { Ran = false, Succeeded = true };

        // Plan 8 item 4d: the timer declining to fire requests at a session that is known to be
        // dead. Ran = false because nothing was attempted, and Succeeded = false because the
        // caller should not read this as a quiet success - but no request was made, and in
        // particular no /auth/login attempt, which is the property the whole 401 design exists
        // to guarantee.
        public static SyncRunResult AuthenticationNeeded(string message) => new()
        {
            Ran = false,
            Succeeded = false,
            AuthenticationRequired = true,
            ErrorMessage = message
        };
    }
}
