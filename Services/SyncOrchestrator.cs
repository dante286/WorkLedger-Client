using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using WorkLedger_Client.Models;
using WorkLedger_Client.Sync;

namespace WorkLedger_Client.Services
{
    // Drains the local SyncQueue through an ISyncProvider and merges what comes back.
    //
    // Conflict resolution (last-write-wins): the remote change wins if its Version is
    // strictly greater than the local row's current Version (a missing local row counts
    // as Version 0, so any remote Version >= 1 wins). On an exact Version tie, local
    // wins and the remote change is discarded outright — two rows only ever reach the
    // same Version by having gone through the same sequence of local mutations, so a tie
    // has nothing new to offer and this also makes replayed/duplicate remote changes
    // idempotent. A remote change whose OriginDeviceId matches this device's own
    // DeviceId is skipped before any Version comparison — it's an echo of a change this
    // device already authored, not an incoming update.
    //
    // Entity identity (WL-47/WL-54): merge lookups match local rows by SyncId, not by
    // the local [PrimaryKey, AutoIncrement] Id. Two independently-offline devices can
    // land unrelated entities on the same local Id; SyncId is generated once at creation
    // and never reassigned, so it's the only value stable across devices. When a
    // RemoteChange's SyncId has no local match, the incoming entity's Id is left at 0 so
    // SaveXAsync inserts a new row (getting its own fresh local Id) rather than updating
    // one that doesn't exist.
    public class SyncOrchestrator
    {
        private readonly LocalDbService _db;
        private readonly ISyncProvider _provider;
        private readonly Timer _syncTimer = new();

        private DateTime _lastSyncedAt = DateTime.MinValue;

        public SyncOrchestrator(LocalDbService db, ISyncProvider provider)
        {
            _db = db;
            _provider = provider;
            _syncTimer.Elapsed += OnSyncElapsed;
        }

        public async Task RunSyncAsync()
        {
            await PushPendingChangesAsync();
            await PullAndMergeRemoteChangesAsync();
            await PruneSyncedQueueAsync();
        }

        private async Task PushPendingChangesAsync()
        {
            var pending = await _db.Connection.Table<SyncQueue>()
                .Where(q => q.IsSynced == false)
                .ToListAsync();

            if (pending.Count == 0)
                return;

            var result = await _provider.PushAsync(pending);
            if (result.AcceptedSyncQueueIds.Count == 0)
                return;

            var acceptedIds = new HashSet<int>(result.AcceptedSyncQueueIds);
            foreach (var entry in pending.Where(entry => acceptedIds.Contains(entry.Id)))
            {
                entry.IsSynced = true;
                await _db.UpdateAsync(entry);
            }
        }

        private async Task PullAndMergeRemoteChangesAsync()
        {
            var syncStartedAt = DateTime.UtcNow;
            var remoteChanges = await _provider.PullAsync(_lastSyncedAt);

            foreach (var change in remoteChanges)
                await ApplyRemoteChangeAsync(change);

            _lastSyncedAt = syncStartedAt;
        }

        // WL-49/WL-60: nothing else in the sync pipeline prunes SyncQueue, so it would
        // otherwise grow without bound for the life of the install. Rows are only useful
        // until a push accepts them; once IsSynced = true there's no further reason to
        // keep them, so this deletes them outright rather than archiving (no requirement
        // yet to keep a history of synced changes). Runs every RunSyncAsync cycle, so on
        // a periodic timer this is itself the "periodically" in "periodically prune".
        private async Task PruneSyncedQueueAsync()
        {
            var synced = await _db.Connection.Table<SyncQueue>()
                .Where(q => q.IsSynced == true)
                .ToListAsync();

            foreach (var entry in synced)
                await _db.DeleteAsync(entry);
        }

        private Task ApplyRemoteChangeAsync(RemoteChange change)
        {
            if (!string.IsNullOrEmpty(change.OriginDeviceId) && change.OriginDeviceId == _db.DeviceId)
                return Task.CompletedTask;

            return change.EntityType switch
            {
                LocalDbService.EntityTypes.Project => ApplyProjectChangeAsync(change),
                LocalDbService.EntityTypes.Task => ApplyTaskChangeAsync(change),
                LocalDbService.EntityTypes.TimeEntry => ApplyTimeEntryChangeAsync(change),
                _ => Task.CompletedTask
            };
        }

        private async Task ApplyProjectChangeAsync(RemoteChange change)
        {
            var local = await _db.Connection.Table<Project>().Where(p => p.SyncId == change.EntityId).FirstOrDefaultAsync();
            if (!RemoteWins(change.Version, local?.Version))
                return;

            if (change.Operation == LocalDbService.SyncOperations.Delete)
            {
                if (local != null)
                    await _db.DeleteProjectAsync(local);
                return;
            }

            var incoming = DeserializePayload<Project>(change.PayloadJson) ?? local ?? new Project();
            incoming.Id = local?.Id ?? 0;
            incoming.SyncId = change.EntityId;
            incoming.Version = change.Version;
            await _db.SaveProjectAsync(incoming);
        }

        private async Task ApplyTaskChangeAsync(RemoteChange change)
        {
            var local = await _db.Connection.Table<TaskItem>().Where(t => t.SyncId == change.EntityId).FirstOrDefaultAsync();
            if (!RemoteWins(change.Version, local?.Version))
                return;

            if (change.Operation == LocalDbService.SyncOperations.Delete)
            {
                if (local != null)
                    await _db.DeleteTaskAsync(local);
                return;
            }

            var incoming = DeserializePayload<TaskItem>(change.PayloadJson) ?? local ?? new TaskItem();
            incoming.Id = local?.Id ?? 0;
            incoming.SyncId = change.EntityId;
            incoming.Version = change.Version;
            await _db.SaveTaskAsync(incoming);
        }

        private async Task ApplyTimeEntryChangeAsync(RemoteChange change)
        {
            var local = await _db.Connection.Table<TimeEntry>().Where(t => t.SyncId == change.EntityId).FirstOrDefaultAsync();
            if (!RemoteWins(change.Version, local?.Version))
                return;

            if (change.Operation == LocalDbService.SyncOperations.Delete)
            {
                if (local != null)
                    await _db.DeleteTimeEntryAsync(local);
                return;
            }

            var incoming = DeserializePayload<TimeEntry>(change.PayloadJson) ?? local ?? new TimeEntry();
            incoming.Id = local?.Id ?? 0;
            incoming.SyncId = change.EntityId;
            incoming.Version = change.Version;
            await _db.SaveTimeEntryAsync(incoming);
        }

        // Missing local row counts as Version 0; a full tie leaves local untouched.
        private static bool RemoteWins(int remoteVersion, int? localVersion)
            => remoteVersion > (localVersion ?? 0);

        private static T? DeserializePayload<T>(string? payloadJson) where T : class
            => string.IsNullOrEmpty(payloadJson) ? null : JsonSerializer.Deserialize<T>(payloadJson);

        private async void OnSyncElapsed(object? sender, ElapsedEventArgs e)
        {
            try
            {
                await RunSyncAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Sync tick failed: {ex.Message}");
            }
        }

        // Mirrors TimerService.ConfigureBackupInterval's shape. A zero/negative interval
        // stops the timer; RunSyncAsync itself remains callable directly for a manual
        // "sync now" entry point regardless of whether the periodic timer is running.
        public void ConfigureSyncInterval(TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero)
            {
                _syncTimer.Stop();
                return;
            }

            _syncTimer.Interval = interval.TotalMilliseconds;
            _syncTimer.AutoReset = true;
            _syncTimer.Start();
        }

        public void StopSync() => _syncTimer.Stop();
    }
}
