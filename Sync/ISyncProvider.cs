using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Sync
{
    // Transport contract for moving SyncQueue entries to/from a remote store.
    // SyncOrchestrator drives push-then-pull against whichever provider it's given;
    // NullSyncProvider is the no-op default until Plan 5/6 add a real one.
    public interface ISyncProvider
    {
        Task<SyncResult> PushAsync(IEnumerable<SyncQueue> pendingChanges);

        Task<IEnumerable<RemoteChange>> PullAsync(DateTime since);
    }
}
