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
    //
    // Parent links (WL-91): the same argument applies one level down, to TaskItem.ProjectId
    // and TimeEntry.TaskItemId. Those ints are the authoring device's and are always
    // recomputed here from the payload's ProjectSyncId/TaskSyncId, never merged through.
    // Changes are applied parents-first so a batch carrying all three types resolves in one
    // pass, and RelinkOrphanedChildrenAsync catches the rest when a child arrives ahead of
    // its parent.
    public class SyncOrchestrator
    {
        private readonly LocalDbService _db;
        private readonly ISyncProvider _provider;
        private readonly ISyncCursorStore? _cursorStore;
        private readonly Timer _syncTimer = new();

        private DateTime _lastSyncedAt;

        // WL-92: cursorStore is optional so a caller with nothing to persist to (tests) gets
        // in-memory behaviour, but the app passes one - otherwise the cursor resets to
        // DateTime.MinValue on every launch and each start re-pulls the entire remote
        // history. That stays *correct*, since the version merge discards what it already
        // has, but the cost grows without bound as history accumulates.
        public SyncOrchestrator(LocalDbService db, ISyncProvider provider, ISyncCursorStore? cursorStore = null)
        {
            _db = db;
            _provider = provider;
            _cursorStore = cursorStore;
            _lastSyncedAt = cursorStore?.LastSyncedAtUtc ?? DateTime.MinValue;
            _syncTimer.Elapsed += OnSyncElapsed;
        }

        // The cursor this device will pass as the next pull's `since`.
        public DateTime LastSyncedAt => _lastSyncedAt;

        public async Task RunSyncAsync()
        {
            var push = await PushPendingChangesAsync();

            // The remote won a conflict, so our copy of that entity is stale and the remote
            // will not re-announce it (a losing item performs no remote write, precisely so
            // it doesn't re-broadcast to every device - which also means its timestamp is
            // already behind our cursor). Rewinding forces the next pull to re-deliver it,
            // stating the operation rather than making us infer it from a payload. The
            // version merge makes the extra changes cheap: everything already held loses its
            // comparison and is discarded. See SyncResult.RequiresFullPull and WL-98.
            if (push.RequiresFullPull)
            {
                Debug.WriteLine("Remote won a push conflict; rewinding the pull cursor for a full re-pull.");
                _lastSyncedAt = DateTime.MinValue;
            }

            await PullAndMergeRemoteChangesAsync();
            await PruneSyncedQueueAsync();
        }

        private async Task<SyncResult> PushPendingChangesAsync()
        {
            var pending = await _db.Connection.Table<SyncQueue>()
                .Where(q => q.IsSynced == false)
                .ToListAsync();

            if (pending.Count == 0)
                return new SyncResult();

            var result = await _provider.PushAsync(pending);

            // WL-94: a row the provider *rejected* is settled too. The remote refuses either
            // because the payload is invalid or because its own version won, and neither
            // changes on a re-push - so leaving these pending would re-push them on every
            // sync forever. Only rows the provider said nothing about (a transport failure,
            // where nothing was decided) stay queued for the next tick.
            var settledIds = new HashSet<int>(result.AcceptedSyncQueueIds);
            settledIds.UnionWith(result.RejectedSyncQueueIds);

            foreach (var entry in pending.Where(entry => settledIds.Contains(entry.Id)))
            {
                entry.IsSynced = true;
                await _db.UpdateAsync(entry);
            }

            return result;
        }

        private async Task PullAndMergeRemoteChangesAsync()
        {
            var pull = await _provider.PullAsync(_lastSyncedAt);

            // A failed pull is not an empty one. Advancing the cursor past changes that were
            // never delivered would skip them permanently, so a failure leaves it alone and
            // the next tick asks for the same window again.
            if (!pull.Succeeded)
            {
                Debug.WriteLine($"Sync pull failed, cursor unchanged: {pull.ErrorMessage}");
                return;
            }

            foreach (var change in OrderParentsFirst(pull.Changes))
                await ApplyRemoteChangeAsync(change);

            await RelinkOrphanedChildrenAsync();

            // WL-92: the provider's clock, not ours. A device running fast would otherwise
            // ask for changes since a moment the server hasn't reached and miss the window.
            _lastSyncedAt = pull.ServerTime;
            _cursorStore?.Save(_lastSyncedAt);
        }

        // WL-91: a child's parent link is resolved from the parent's SyncId at merge time,
        // so a task merged before its project can't find it. Applying projects -> tasks ->
        // time entries makes the common case (a batch holding all three) resolve on the
        // first pass. This mirrors the ordering the server applies a push batch in, and is
        // a stable sort, so several changes for one entity keep their relative order.
        private static IEnumerable<RemoteChange> OrderParentsFirst(IEnumerable<RemoteChange> changes)
            => changes.OrderBy(change => change.EntityType switch
            {
                LocalDbService.EntityTypes.Project => 0,
                LocalDbService.EntityTypes.Task => 1,
                LocalDbService.EntityTypes.TimeEntry => 2,
                _ => 3
            });

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

            // WL-91: ProjectId as received is the *authoring* device's local autoincrement
            // value and means nothing here, so it is always overwritten - never merged
            // through. ProjectSyncId is the half that travels; 0 marks an orphan for
            // RelinkOrphanedChildrenAsync to pick up once the project arrives.
            incoming.ProjectId = await ResolveLocalProjectIdAsync(incoming.ProjectSyncId);
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

            // WL-91: see the note in ApplyTaskChangeAsync - same rule one level down.
            incoming.TaskItemId = await ResolveLocalTaskIdAsync(incoming.TaskSyncId);
            await _db.SaveTimeEntryAsync(incoming);
        }

        // Map a cross-device parent SyncId to whatever local autoincrement Id that parent
        // happens to hold on this device. 0 - the value an unset foreign key already has -
        // means the parent isn't here yet. Written out per type rather than generically
        // because IVersionedEntity has no Id, and sqlite-net resolves Table<T> predicates
        // by reflecting over the concrete mapped type.
        private async Task<int> ResolveLocalProjectIdAsync(Guid projectSyncId)
        {
            if (projectSyncId == Guid.Empty)
                return 0;

            var parent = await _db.Connection.Table<Project>()
                .Where(p => p.SyncId == projectSyncId)
                .FirstOrDefaultAsync();
            return parent?.Id ?? 0;
        }

        private async Task<int> ResolveLocalTaskIdAsync(Guid taskSyncId)
        {
            if (taskSyncId == Guid.Empty)
                return 0;

            var parent = await _db.Connection.Table<TaskItem>()
                .Where(t => t.SyncId == taskSyncId)
                .FirstOrDefaultAsync();
            return parent?.Id ?? 0;
        }

        // WL-91: a child whose parent hadn't synced in yet was stored with its foreign key
        // at 0, which makes it invisible to every GetXByYIdAsync query - permanently, since
        // nothing would revisit it. Its ProjectSyncId/TaskSyncId still records which parent
        // it wants, so once that parent lands this re-links it. Runs after every merge pass
        // and is a no-write no-op when there are no orphans.
        //
        // Writes through the raw UpdateAsync rather than SaveXAsync deliberately: re-linking
        // repairs a device-local column, and nothing about the entity changed that another
        // device could act on. Going through SaveXAsync would bump the Version and queue a
        // sync entry, re-broadcasting a purely local repair to every peer.
        private async Task RelinkOrphanedChildrenAsync()
        {
            var orphanedTasks = await _db.Connection.Table<TaskItem>()
                .Where(t => t.ProjectId == 0)
                .ToListAsync();

            foreach (var task in orphanedTasks)
            {
                var projectId = await ResolveLocalProjectIdAsync(task.ProjectSyncId);
                if (projectId == 0)
                    continue;

                task.ProjectId = projectId;
                await _db.UpdateAsync(task);
            }

            var orphanedEntries = await _db.Connection.Table<TimeEntry>()
                .Where(e => e.TaskItemId == 0)
                .ToListAsync();

            foreach (var entry in orphanedEntries)
            {
                var taskId = await ResolveLocalTaskIdAsync(entry.TaskSyncId);
                if (taskId == 0)
                    continue;

                entry.TaskItemId = taskId;
                await _db.UpdateAsync(entry);
            }
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
