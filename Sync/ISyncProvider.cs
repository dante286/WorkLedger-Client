using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Sync
{
    // Transport contract for moving SyncQueue entries to/from a remote store.
    // SyncOrchestrator drives push-then-pull against whichever provider it's given;
    // NullSyncProvider is the no-op default until a SyncMode setting selects a real one.
    //
    // Implementations are expected to be total: a transport failure comes back as a
    // failure-flagged SyncResult/PullResult, not as a thrown exception. See
    // SyncProviderException for the one type anything that does throw should use.
    public interface ISyncProvider
    {
        Task<SyncResult> PushAsync(IEnumerable<SyncQueue> pendingChanges);

        // `since` is the cursor from the previous call's PullResult.ServerTime - not this
        // device's clock. See PullResult and WL-92.
        Task<PullResult> PullAsync(DateTime since);
    }
}
