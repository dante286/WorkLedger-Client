using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Timers;
using Timer = System.Timers.Timer;
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
    // RemoteChange's SyncId has no local match, the incoming entity's Id is left at 0 and the
    // row is inserted (getting its own fresh local Id) rather than updated.
    //
    // WL-99. Merging is not editing: an applied remote change keeps the author's Version and
    // OriginDeviceId and enqueues nothing. It is written through the raw generic
    // Insert/Update/Delete helpers rather than LocalDbService's SaveXAsync/DeleteXAsync, which
    // are the local-edit path and would bump the version, claim authorship, and queue the change
    // straight back out. See WriteMergedAsync for what that cost.
    //
    // Parent links (WL-91): the same argument applies one level down, to TaskItem.ProjectId
    // and TimeEntry.TaskItemId. Those ints are the authoring device's and are always
    // recomputed here from the payload's ProjectSyncId/TaskSyncId, never merged through.
    // Changes are applied parents-first so a batch carrying all three types resolves in one
    // pass, and RelinkOrphanedChildrenAsync catches the rest when a child arrives ahead of
    // its parent.
    //
    // WL-101: a pull is not the only source of remote changes. When a push is rejected because
    // the remote's own row won, the provider hands that winner back on SyncResult, and it is
    // merged here through the same MergeRemoteChangesAsync path a pulled change takes. Only a
    // winner that arrives without its operation stated falls back to RequiresFullPull's cursor
    // rewind.
    //
    // WL-51: the merge has no undo, so an optional caller-supplied hook takes a safety backup
    // immediately before the first remote change of a run overwrites or deletes a local row.
    // See EnsurePreMergeBackupAsync for why it's once per run and why inserts don't count.
    //
    // WL-134 (plan 8 item 4d): the TIMER path asks the provider whether there is a usable session
    // before doing anything, and skips the whole run when there isn't. A revoked install would
    // otherwise generate a doomed round trip every 15 minutes forever. A manual "sync now"
    // deliberately does NOT check - it is a person asking, a revocation that was really a server
    // restart should get the chance to heal, and finding out costs one authenticated request and
    // zero login attempts.
    public class SyncOrchestrator
    {
        private readonly LocalDbService _db;
        private readonly ISyncProvider _provider;
        private readonly ISyncCursorStore? _cursorStore;
        private readonly Func<Task>? _preMergeBackupAsync;
        private readonly Timer _syncTimer = new();
        private readonly SemaphoreSlim _runLock = new(1, 1);

        private DateTime _lastSyncedAt;

        // WL-51: reset at the top of every RunSyncAsync, so the backup hook fires at most once
        // per run - see EnsurePreMergeBackupAsync.
        private bool _preMergeBackupTakenThisRun;

        // WL-92: cursorStore is optional so a caller with nothing to persist to (tests) gets
        // in-memory behaviour, but the app passes one - otherwise the cursor resets to
        // DateTime.MinValue on every launch and each start re-pulls the entire remote
        // history. That stays *correct*, since the version merge discards what it already
        // has, but the cost grows without bound as history accumulates.
        //
        // WL-51: preMergeBackupAsync is invoked at most once per RunSyncAsync, immediately
        // before the first remote change that overwrites or deletes a local row. The *policy*
        // (AppSettings.PreMergeBackupMode) lives in the delegate the caller supplies rather
        // than here - this type only knows when the risky moment is, not whether the user
        // wants a copy taken. Null means "no backup", which is what the tests use.
        public SyncOrchestrator(
            LocalDbService db,
            ISyncProvider provider,
            ISyncCursorStore? cursorStore = null,
            Func<Task>? preMergeBackupAsync = null)
        {
            _db = db;
            _provider = provider;
            _cursorStore = cursorStore;
            _preMergeBackupAsync = preMergeBackupAsync;
            _lastSyncedAt = cursorStore?.LastSyncedAtUtc ?? DateTime.MinValue;
            _syncTimer.Elapsed += OnSyncElapsed;
        }

        // The cursor this device will pass as the next pull's `since`.
        public DateTime LastSyncedAt => _lastSyncedAt;

        /// <summary>
        /// The background tick's entry point. Plan 8 item 4d: no usable session, no requests.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="RunSyncAsync"/> rather than a flag on it because the two
        /// callers genuinely want different things. An unattended timer firing at a dead session
        /// achieves nothing and, in the version of this design we did not build, would be the
        /// thing that locks a sleeping user out of their own server. A person pressing "sync now"
        /// has asked, and is owed the attempt.
        /// <para>
        /// Note this can only ever <i>reduce</i> what the client does. It is not where the lockout
        /// guarantee comes from - that is Invariant L, inside the provider, which holds even if
        /// this check were deleted.
        /// </para>
        /// </remarks>
        public Task<SyncRunResult> RunTimerSyncAsync()
        {
            if (_provider is IAuthenticatingSyncProvider authenticating && !authenticating.Auth.CanSync)
            {
                Debug.WriteLine($"Skipping the scheduled sync: auth state is {authenticating.Auth.State}.");
                return Task.FromResult(SyncRunResult.AuthenticationNeeded(
                    "Sync is paused until you sign in again."));
            }

            return RunSyncAsync();
        }

        // A sync run already in flight is never joined by a second one: the timer tick and a
        // manual "sync now" would otherwise interleave a push and a merge over the same queue
        // and the same rows. WaitAsync(0) skips rather than queues - by the time a backed-up
        // run got its turn it would be re-doing work the run ahead of it had just done.
        public async Task<SyncRunResult> RunSyncAsync()
        {
            if (!await _runLock.WaitAsync(0))
            {
                Debug.WriteLine("Sync already in progress; skipping this run.");
                return SyncRunResult.Skipped();
            }

            try
            {
                _preMergeBackupTakenThisRun = false;

                var push = await PushPendingChangesAsync();

                // WL-101: the remote won these conflicts and handed the winners straight back,
                // each stating whether it's a live row or a tombstone. That is everything the
                // merge needs, so they go through the same path a pulled change takes and the
                // conflict costs one merge instead of a re-pull of the account's history.
                if (push.AuthoritativeChanges.Count > 0)
                    await MergeRemoteChangesAsync(push.AuthoritativeChanges);

                // The fallback, for a winner that arrived without its operation stated. Our copy
                // of that entity is stale and the remote will not re-announce it (a losing item
                // performs no remote write, precisely so it doesn't re-broadcast to every device
                // - which also means its timestamp is already behind our cursor). Rewinding
                // forces the next pull to re-deliver it, stating the operation rather than making
                // us infer it from a payload. The version merge makes the extra changes cheap:
                // everything already held loses its comparison and is discarded. See
                // SyncResult.RequiresFullPull.
                if (push.RequiresFullPull)
                {
                    Debug.WriteLine("Remote won a push conflict without stating the operation; rewinding the pull cursor for a full re-pull.");
                    _lastSyncedAt = DateTime.MinValue;
                }

                var pull = await PullAndMergeRemoteChangesAsync();
                await PruneSyncedQueueAsync();

                var pushFailure = push.Failures.FirstOrDefault();
                return new SyncRunResult
                {
                    Ran = true,
                    Succeeded = pushFailure is null && pull.Succeeded,
                    ErrorMessage = pushFailure?.ErrorMessage ?? pull.ErrorMessage,
                    // WL-134: either half discovering there is no session is enough. The push runs
                    // first, so on a revoked token it is usually the one that finds out - but a
                    // run with an empty queue pushes nothing, and then the pull is the only thing
                    // that could have noticed.
                    AuthenticationRequired = push.AuthenticationRequired || pull.AuthenticationRequired,
                    PushedCount = push.AcceptedSyncQueueIds.Count + push.RejectedSyncQueueIds.Count,
                    // Adopted push winners are remote changes this run merged too, so they count
                    // as received - otherwise a run that resolved a conflict reports nothing.
                    ReceivedCount = pull.Changes.Count + push.AuthoritativeChanges.Count
                };
            }
            finally
            {
                _runLock.Release();
                SyncCompleted?.Invoke(this, EventArgs.Empty);
            }
        }

        // Raised after every completed run, timer-driven or manual, so a UI showing the last
        // synced time doesn't have to poll for it.
        public event EventHandler? SyncCompleted;

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

        private async Task<PullResult> PullAndMergeRemoteChangesAsync()
        {
            var pull = await _provider.PullAsync(_lastSyncedAt);

            // A failed pull is not an empty one. Advancing the cursor past changes that were
            // never delivered would skip them permanently, so a failure leaves it alone and
            // the next tick asks for the same window again.
            if (!pull.Succeeded)
            {
                Debug.WriteLine($"Sync pull failed, cursor unchanged: {pull.ErrorMessage}");
                return pull;
            }

            await MergeRemoteChangesAsync(pull.Changes);

            // WL-92: the provider's clock, not ours. A device running fast would otherwise
            // ask for changes since a moment the server hasn't reached and miss the window.
            _lastSyncedAt = pull.ServerTime;
            _cursorStore?.Save(_lastSyncedAt);
            return pull;
        }

        // The one way a remote change enters this database, whether it was pulled or handed back
        // as the winner of a push conflict (WL-101). Keeping both on this method is the point:
        // an adopted winner gets the parents-first ordering, the version comparison, the
        // pre-merge backup and the orphan re-link pass, exactly as a pulled change does, and
        // neither route can quietly grow behaviour the other lacks.
        private async Task MergeRemoteChangesAsync(IEnumerable<RemoteChange> changes)
        {
            foreach (var change in OrderParentsFirst(changes))
                await ApplyRemoteChangeAsync(change);

            await RelinkOrphanedChildrenAsync();
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
                {
                    await EnsurePreMergeBackupAsync();
                    await _db.DeleteAsync(local);
                }
                return;
            }

            var incoming = DeserializePayload<Project>(change.PayloadJson) ?? local ?? new Project();
            incoming.Id = local?.Id ?? 0;
            StampFromRemote(incoming, change);
            if (local != null)
                await EnsurePreMergeBackupAsync();

            await WriteMergedAsync(incoming, isInsert: local is null);
        }

        private async Task ApplyTaskChangeAsync(RemoteChange change)
        {
            var local = await _db.Connection.Table<TaskItem>().Where(t => t.SyncId == change.EntityId).FirstOrDefaultAsync();
            if (!RemoteWins(change.Version, local?.Version))
                return;

            if (change.Operation == LocalDbService.SyncOperations.Delete)
            {
                if (local != null)
                {
                    await EnsurePreMergeBackupAsync();
                    await _db.DeleteAsync(local);
                }
                return;
            }

            var incoming = DeserializePayload<TaskItem>(change.PayloadJson) ?? local ?? new TaskItem();
            incoming.Id = local?.Id ?? 0;
            StampFromRemote(incoming, change);

            // WL-91: ProjectId as received is the *authoring* device's local autoincrement
            // value and means nothing here, so it is always overwritten - never merged
            // through. ProjectSyncId is the half that travels; 0 marks an orphan for
            // RelinkOrphanedChildrenAsync to pick up once the project arrives.
            incoming.ProjectId = await ResolveLocalProjectIdAsync(incoming.ProjectSyncId);
            if (local != null)
                await EnsurePreMergeBackupAsync();

            await WriteMergedAsync(incoming, isInsert: local is null);
        }

        private async Task ApplyTimeEntryChangeAsync(RemoteChange change)
        {
            var local = await _db.Connection.Table<TimeEntry>().Where(t => t.SyncId == change.EntityId).FirstOrDefaultAsync();
            if (!RemoteWins(change.Version, local?.Version))
                return;

            if (change.Operation == LocalDbService.SyncOperations.Delete)
            {
                if (local != null)
                {
                    await EnsurePreMergeBackupAsync();
                    await _db.DeleteAsync(local);
                }
                return;
            }

            var incoming = DeserializePayload<TimeEntry>(change.PayloadJson) ?? local ?? new TimeEntry();
            incoming.Id = local?.Id ?? 0;
            StampFromRemote(incoming, change);

            // WL-91: see the note in ApplyTaskChangeAsync - same rule one level down.
            incoming.TaskItemId = await ResolveLocalTaskIdAsync(incoming.TaskSyncId);
            if (local != null)
                await EnsurePreMergeBackupAsync();

            await WriteMergedAsync(incoming, isInsert: local is null);
        }

        // WL-51: the last-write-wins merge has no undo. Once a remote change with a higher
        // Version overwrites or deletes a local row, the previous local state is gone - and a
        // misbehaving peer or a mis-restored database can carry a version high enough to win
        // every comparison it meets. This takes a copy of the database *before* the first such
        // write in a run, so there is something to restore from.
        //
        // Once per run, not once per overwrite: a run typically applies zero of these and
        // occasionally a handful at once, so the first one is the moment worth snapshotting.
        // Inserts deliberately don't trigger it - a remote row this device has never seen
        // destroys nothing. A failing backup is logged and does not abort the merge: refusing
        // to sync because the backup folder is unwritable would be a worse outcome than the
        // one being guarded against.
        private async Task EnsurePreMergeBackupAsync()
        {
            if (_preMergeBackupAsync is null || _preMergeBackupTakenThisRun)
                return;

            _preMergeBackupTakenThisRun = true;

            try
            {
                await _preMergeBackupAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Pre-merge backup failed, continuing with the merge: {ex.Message}");
            }
        }

        // WL-99: carries the change's own identity and versioning onto the row about to be written.
        //
        // Version and OriginDeviceId are the *authoring* device's and are kept verbatim. That is
        // what makes the merge idempotent: the next copy of this same change arrives at the same
        // Version, loses the RemoteWins comparison, and is discarded. Re-stamping either one makes
        // the local copy look like a newer, locally-authored edit instead.
        private static void StampFromRemote(IVersionedEntity incoming, RemoteChange change)
        {
            incoming.SyncId = change.EntityId;
            incoming.Version = change.Version;

            // Only overwrite when the change actually names an author - a provider that couldn't
            // lift OriginDeviceId out of the payload sends null/empty, and the payload's own value
            // (already deserialized onto `incoming`) is the better answer than blanking it.
            if (!string.IsNullOrEmpty(change.OriginDeviceId))
                incoming.OriginDeviceId = change.OriginDeviceId;
        }

        // WL-99: applies a merged row through the raw generic helpers, never SaveXAsync/DeleteXAsync.
        //
        // Those exist to record a *local* edit: they bump Version, restamp OriginDeviceId to this
        // device, and enqueue a SyncQueue row. None of that is right for a change another device
        // authored and already broadcast. Routing a merge through them re-announced every incoming
        // change as a fresh local edit one version higher - so the echo beat the original, the peer
        // merged it and bumped again, and two idle devices traded ever-higher versions of an
        // unchanged row indefinitely, each round costing a push and a pull.
        //
        // Same rule, same mechanism as RelinkOrphanedChildrenAsync below.
        private Task<int> WriteMergedAsync<T>(T incoming, bool isInsert) where T : new()
            => isInsert ? _db.InsertAsync(incoming) : _db.UpdateAsync(incoming);

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
                await RunTimerSyncAsync();
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
