using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using Xunit;

namespace WorkLedger.Tests
{
    // WL-97: there was no TimerService test at all, which is exactly why the service could
    // write time entries straight past the sync queue for two plans without anyone noticing.
    // The LocalDbService tests all drive LocalDbService directly, so they proved the queueing
    // worked while the app's main caller wasn't using it.
    public class TimerServiceTests : IAsyncLifetime
    {
        private const string TestDeviceId = "timer-test-device";

        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"WorkLedgerTimerTests_{Guid.NewGuid()}.db");
        private LocalDbService _db = null!;
        private AppStateService _state = null!;
        private TimerService _timer = null!;

        public async Task InitializeAsync()
        {
            _db = new LocalDbService(_dbPath, TestDeviceId);
            await _db.Initialization;
            _state = new AppStateService();
            _timer = new TimerService(_db, _state);
        }

        public async Task DisposeAsync()
        {
            await _timer.StopTimerAsync();
            await _db.Connection.CloseAsync();
            TryDeleteWithRetries(_dbPath);
        }

        private static void TryDeleteWithRetries(string path, int maxAttempts = 5)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
            }
        }

        // A saved project and task, so the time entry has a real parent to resolve against
        // rather than a dangling id.
        private async Task<TaskItem> SeedTaskAsync(bool isCompleted = false)
        {
            var project = new Project { Name = "Timer Project" };
            await _db.SaveProjectAsync(project);

            var task = new TaskItem { Name = "Timer Task", ProjectId = project.Id, IsCompleted = isCompleted };
            await _db.SaveTaskAsync(task);
            return task;
        }

        // Only the queue rows the timer produced - seeding a project and task queues rows too.
        private async Task<System.Collections.Generic.List<SyncQueue>> TimeEntryQueueRowsAsync()
        {
            var queue = await _db.GetAllAsync<SyncQueue>();
            return queue.Where(q => q.EntityType == "timeentry").OrderBy(q => q.Id).ToList();
        }

        [Fact]
        public async Task StartTimerAsync_QueuesACreateForTheNewTimeEntry()
        {
            var task = await SeedTaskAsync();

            await _timer.StartTimerAsync(task);

            var queued = Assert.Single(await TimeEntryQueueRowsAsync());
            Assert.Equal("create", queued.Operation);
            Assert.Equal(_state.ActiveTimeEntry!.SyncId, queued.EntityId);
            Assert.False(queued.IsSynced);
        }

        [Fact]
        public async Task StartTimerAsync_StampsTheEntryWithVersionAndOriginDevice()
        {
            var task = await SeedTaskAsync();

            await _timer.StartTimerAsync(task);

            var stored = await _db.GetByIdAsync<TimeEntry>(_state.ActiveTimeEntry!.Id);
            Assert.Equal(1, stored.Version);
            Assert.Equal(TestDeviceId, stored.OriginDeviceId);
            Assert.NotEqual(Guid.Empty, stored.SyncId);
        }

        // WL-91's parent link has to survive the timer path too, or every timed entry pushes
        // as an orphan and the server rejects it.
        [Fact]
        public async Task StartTimerAsync_ResolvesTheParentTaskSyncId()
        {
            var task = await SeedTaskAsync();

            await _timer.StartTimerAsync(task);

            var stored = await _db.GetByIdAsync<TimeEntry>(_state.ActiveTimeEntry!.Id);
            Assert.Equal(task.SyncId, stored.TaskSyncId);

            // And it's in the payload that actually ships, not just the local row.
            var queued = Assert.Single(await TimeEntryQueueRowsAsync());
            var payload = JsonSerializer.Deserialize<TimeEntry>(queued.PayloadJson!);
            Assert.Equal(task.SyncId, payload!.TaskSyncId);
        }

        [Fact]
        public async Task StopTimerAsync_QueuesAnUpdateAtABumpedVersion()
        {
            var task = await SeedTaskAsync();
            await _timer.StartTimerAsync(task);
            var entryId = _state.ActiveTimeEntry!.Id;

            await _timer.StopTimerAsync();

            var queued = await TimeEntryQueueRowsAsync();
            Assert.Equal(2, queued.Count);
            Assert.Equal(new[] { "create", "update" }, queued.Select(q => q.Operation));

            // The bump is the point: an unversioned stop loses the receiving device's strict
            // > comparison, so the entry stays open on every other device forever.
            var stored = await _db.GetByIdAsync<TimeEntry>(entryId);
            Assert.Equal(2, stored.Version);
            Assert.NotNull(stored.EndTime);
            Assert.NotNull(stored.DurationSeconds);
        }

        [Fact]
        public async Task StopTimerAsync_ClearsTheActiveState()
        {
            var task = await SeedTaskAsync();
            await _timer.StartTimerAsync(task);

            await _timer.StopTimerAsync();

            Assert.Null(_state.ActiveTask);
            Assert.Null(_state.ActiveTimeEntry);
            Assert.False(_timer.IsRunning);
        }

        [Fact]
        public async Task StopTimerAsync_WithNoRunningTimer_DoesNothing()
        {
            await _timer.StopTimerAsync();

            Assert.Empty(await TimeEntryQueueRowsAsync());
        }

        // Only one timer runs at a time, so starting a second stops the first - which has to
        // queue the first entry's completion, not silently abandon it.
        [Fact]
        public async Task StartTimerAsync_WhileAnotherIsRunning_StopsAndQueuesTheFirstEntry()
        {
            var first = await SeedTaskAsync();
            var second = new TaskItem { Name = "Second Task", ProjectId = first.ProjectId };
            await _db.SaveTaskAsync(second);

            await _timer.StartTimerAsync(first);
            var firstEntryId = _state.ActiveTimeEntry!.Id;

            await _timer.StartTimerAsync(second);

            var storedFirst = await _db.GetByIdAsync<TimeEntry>(firstEntryId);
            Assert.NotNull(storedFirst.EndTime);
            Assert.Equal(2, storedFirst.Version);

            // create (first) -> update (first, on stop) -> create (second).
            var queued = await TimeEntryQueueRowsAsync();
            Assert.Equal(new[] { "create", "update", "create" }, queued.Select(q => q.Operation));
            Assert.Equal(second.SyncId, (await _db.GetByIdAsync<TimeEntry>(_state.ActiveTimeEntry!.Id)).TaskSyncId);
        }

        [Fact]
        public async Task SwitchTaskAsync_QueuesTheStopAndTheStart()
        {
            var first = await SeedTaskAsync();
            var second = new TaskItem { Name = "Second Task", ProjectId = first.ProjectId };
            await _db.SaveTaskAsync(second);
            await _timer.StartTimerAsync(first);

            await _timer.SwitchTaskAsync(second);

            var queued = await TimeEntryQueueRowsAsync();
            Assert.Equal(new[] { "create", "update", "create" }, queued.Select(q => q.Operation));
            Assert.Equal(second.Id, _state.ActiveTimeEntry!.TaskItemId);
        }

        [Fact]
        public async Task StartTimerAsync_ForACompletedTask_DoesNothing()
        {
            var task = await SeedTaskAsync(isCompleted: true);

            await _timer.StartTimerAsync(task);

            Assert.Null(_state.ActiveTimeEntry);
            Assert.Empty(await TimeEntryQueueRowsAsync());
        }

        // The end-to-end statement of the bug: a full start/stop cycle has to leave the sync
        // pipeline something to send. Before WL-97 this queue was empty.
        [Fact]
        public async Task AFullTimedSession_LeavesTheSyncQueueAbleToPushIt()
        {
            var task = await SeedTaskAsync();

            await _timer.StartTimerAsync(task);
            await _timer.StopTimerAsync();

            var queued = await TimeEntryQueueRowsAsync();
            Assert.Equal(2, queued.Count);
            Assert.All(queued, row =>
            {
                Assert.False(row.IsSynced);
                Assert.NotNull(row.PayloadJson);

                var payload = JsonSerializer.Deserialize<TimeEntry>(row.PayloadJson!)!;
                Assert.NotEqual(Guid.Empty, payload.SyncId);
                Assert.NotEqual(Guid.Empty, payload.TaskSyncId);
                Assert.Equal(TestDeviceId, payload.OriginDeviceId);
                // The server rejects Version < 1 outright, so a 0 here would fail per-item.
                Assert.True(payload.Version >= 1);
            });
        }
    }
}
