using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Sync
{
    // No-op ISyncProvider: the default/placeholder until a real provider (Plan 5's
    // FileSyncProvider or Plan 6's ApiSyncProvider) is wired up via a SyncMode setting
    // in Plan 7. Push accepts nothing (there's no remote to accept it), so pending
    // SyncQueue rows are left unsynced and retried later; pull never has anything new.
    public class NullSyncProvider : ISyncProvider
    {
        public Task<SyncResult> PushAsync(IEnumerable<SyncQueue> pendingChanges)
            => Task.FromResult(new SyncResult());

        // Reports success with the local clock: there is no remote, so "nothing new" is the
        // truthful answer rather than a failure, and the cursor may safely advance.
        public Task<PullResult> PullAsync(DateTime since)
            => Task.FromResult(new PullResult { ServerTime = DateTime.UtcNow });
    }
}
