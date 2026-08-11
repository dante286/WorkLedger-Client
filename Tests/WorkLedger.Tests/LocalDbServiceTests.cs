using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using Xunit;

namespace WorkLedger.Tests
{
    public class LocalDbServiceTests : IAsyncLifetime
    {
        private const string TestDeviceId = "device-under-test";

        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"WorkLedgerTests_{Guid.NewGuid()}.db");
        private LocalDbService _service = null!;

        public async Task InitializeAsync()
        {
            _service = new LocalDbService(_dbPath, TestDeviceId);
            // Await the exposed Initialization task instead of relying on the constructor's
            // fire-and-forget timing, so tables and the versioning backfill are ready before
            // any test body runs.
            await _service.Initialization;
        }

        public async Task DisposeAsync()
        {
            await _service.Connection.CloseAsync();
            TryDeleteWithRetries(_dbPath);
        }

        // sqlite-net-pcl pools native connections by path, so the file can still be briefly
        // locked just after CloseAsync returns; retry a few times rather than failing cleanup.
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

        [Fact]
        public async Task SaveProjectAsync_WithZeroId_Inserts()
        {
            var project = new Project { Name = "New Project" };

            await _service.SaveProjectAsync(project);

            Assert.NotEqual(0, project.Id);
            var stored = await _service.GetProjectByIdAsync(project.Id);
            Assert.NotNull(stored);
            Assert.Equal("New Project", stored!.Name);
        }

        [Fact]
        public async Task SaveProjectAsync_WithExistingId_Updates()
        {
            var project = new Project { Name = "Original Name" };
            await _service.SaveProjectAsync(project);
            var insertedId = project.Id;

            project.Name = "Updated Name";
            await _service.SaveProjectAsync(project);

            var stored = await _service.GetProjectByIdAsync(insertedId);
            Assert.NotNull(stored);
            Assert.Equal("Updated Name", stored!.Name);
            Assert.Equal(insertedId, stored.Id);

            var all = await _service.GetProjectsAsync();
            Assert.Single(all);
        }

        [Fact]
        public async Task SaveProjectAsync_WithZeroId_SetsVersionOneAndOriginDeviceId()
        {
            var project = new Project { Name = "New Project" };

            await _service.SaveProjectAsync(project);

            var stored = await _service.GetProjectByIdAsync(project.Id);
            Assert.NotNull(stored);
            Assert.Equal(1, stored!.Version);
            Assert.Equal(TestDeviceId, stored.OriginDeviceId);
        }

        [Fact]
        public async Task SaveProjectAsync_WithExistingId_IncrementsVersionAndRefreshesOriginDeviceId()
        {
            var project = new Project { Name = "Original" };
            await _service.SaveProjectAsync(project);
            Assert.Equal(1, project.Version);

            project.Name = "Updated";
            await _service.SaveProjectAsync(project);

            var stored = await _service.GetProjectByIdAsync(project.Id);
            Assert.NotNull(stored);
            Assert.Equal(2, stored!.Version);
            Assert.Equal(TestDeviceId, stored.OriginDeviceId);
        }

        [Fact]
        public async Task SaveProjectAsync_WithZeroId_EnqueuesCreateSyncEntry()
        {
            var project = new Project { Name = "New Project" };

            await _service.SaveProjectAsync(project);

            var queue = await _service.GetAllAsync<SyncQueue>();
            var entry = Assert.Single(queue);
            Assert.Equal("project", entry.EntityType);
            Assert.Equal(project.SyncId, entry.EntityId);
            Assert.Equal("create", entry.Operation);
            Assert.False(entry.IsSynced);
            Assert.NotNull(entry.PayloadJson);
        }

        [Fact]
        public async Task SaveProjectAsync_WithExistingId_EnqueuesUpdateSyncEntry()
        {
            var project = new Project { Name = "Original" };
            await _service.SaveProjectAsync(project);

            project.Name = "Updated";
            await _service.SaveProjectAsync(project);

            var queue = await _service.GetAllAsync<SyncQueue>();
            Assert.Equal(2, queue.Count);
            var updateEntry = queue.Single(q => q.Operation == "update");
            Assert.Equal("project", updateEntry.EntityType);
            Assert.Equal(project.SyncId, updateEntry.EntityId);
        }

        [Fact]
        public async Task DeleteProjectAsync_EnqueuesDeleteSyncEntry()
        {
            var project = new Project { Name = "To Delete" };
            await _service.SaveProjectAsync(project);

            await _service.DeleteProjectAsync(project);

            var queue = await _service.GetAllAsync<SyncQueue>();
            var deleteEntry = Assert.Single(queue, q => q.Operation == "delete");
            Assert.Equal("project", deleteEntry.EntityType);
            Assert.Equal(project.SyncId, deleteEntry.EntityId);
            Assert.NotNull(deleteEntry.PayloadJson);
        }

        // WL-93: the tombstone has to out-version the copy every peer already holds, or
        // RemoteWins' strict > drops it and the row comes back. Asserted on the queued
        // payload rather than the in-memory entity, since the payload is what ships.
        [Theory]
        [InlineData("project")]
        [InlineData("task")]
        [InlineData("timeentry")]
        public async Task DeleteAsync_QueuesTombstoneAtABumpedVersion(string entityType)
        {
            int versionBeforeDelete;
            switch (entityType)
            {
                case "project":
                    var project = new Project { Name = "To Delete" };
                    await _service.SaveProjectAsync(project);
                    versionBeforeDelete = project.Version;
                    await _service.DeleteProjectAsync(project);
                    break;
                case "task":
                    var task = new TaskItem { Name = "To Delete", ProjectId = 1 };
                    await _service.SaveTaskAsync(task);
                    versionBeforeDelete = task.Version;
                    await _service.DeleteTaskAsync(task);
                    break;
                default:
                    var timeEntry = new TimeEntry { TaskItemId = 1, StartTime = DateTime.UtcNow };
                    await _service.SaveTimeEntryAsync(timeEntry);
                    versionBeforeDelete = timeEntry.Version;
                    await _service.DeleteTimeEntryAsync(timeEntry);
                    break;
            }

            var queue = await _service.GetAllAsync<SyncQueue>();
            var deleteEntry = Assert.Single(queue, q => q.Operation == "delete");
            Assert.Equal(entityType, deleteEntry.EntityType);

            var payload = JsonSerializer.Deserialize<VersionProbe>(deleteEntry.PayloadJson!);
            Assert.NotNull(payload);
            Assert.Equal(versionBeforeDelete + 1, payload!.Version);
            Assert.Equal(TestDeviceId, payload.OriginDeviceId);
        }

        // Reads only the two fields every entity type shares, so one assertion covers all three.
        private class VersionProbe
        {
            public int Version { get; set; }
            public string? OriginDeviceId { get; set; }
        }

        [Fact]
        public async Task SaveTaskAsync_WithZeroId_EnqueuesCreateSyncEntry()
        {
            var task = new TaskItem { Name = "New Task", ProjectId = 1 };

            await _service.SaveTaskAsync(task);

            var queue = await _service.GetAllAsync<SyncQueue>();
            var entry = Assert.Single(queue);
            Assert.Equal("task", entry.EntityType);
            Assert.Equal(task.SyncId, entry.EntityId);
            Assert.Equal("create", entry.Operation);
        }

        [Fact]
        public async Task SaveTaskAsync_WithExistingId_EnqueuesUpdateSyncEntry()
        {
            var task = new TaskItem { Name = "Original", ProjectId = 1 };
            await _service.SaveTaskAsync(task);

            task.Name = "Updated";
            await _service.SaveTaskAsync(task);

            var queue = await _service.GetAllAsync<SyncQueue>();
            var updateEntry = Assert.Single(queue, q => q.Operation == "update");
            Assert.Equal("task", updateEntry.EntityType);
            Assert.Equal(task.SyncId, updateEntry.EntityId);
        }

        [Fact]
        public async Task DeleteTaskAsync_EnqueuesDeleteSyncEntry()
        {
            var task = new TaskItem { Name = "To Delete", ProjectId = 1 };
            await _service.SaveTaskAsync(task);

            await _service.DeleteTaskAsync(task);

            var queue = await _service.GetAllAsync<SyncQueue>();
            var deleteEntry = Assert.Single(queue, q => q.Operation == "delete");
            Assert.Equal("task", deleteEntry.EntityType);
            Assert.Equal(task.SyncId, deleteEntry.EntityId);
        }

        [Fact]
        public async Task SaveTimeEntryAsync_WithZeroId_EnqueuesCreateSyncEntry()
        {
            var entry = new TimeEntry { TaskItemId = 1, StartTime = DateTime.UtcNow };

            await _service.SaveTimeEntryAsync(entry);

            var queue = await _service.GetAllAsync<SyncQueue>();
            var queueEntry = Assert.Single(queue);
            Assert.Equal("timeentry", queueEntry.EntityType);
            Assert.Equal(entry.SyncId, queueEntry.EntityId);
            Assert.Equal("create", queueEntry.Operation);
        }

        [Fact]
        public async Task SaveTimeEntryAsync_WithExistingId_EnqueuesUpdateSyncEntry()
        {
            var entry = new TimeEntry { TaskItemId = 1, StartTime = DateTime.UtcNow };
            await _service.SaveTimeEntryAsync(entry);

            entry.EndTime = DateTime.UtcNow;
            await _service.SaveTimeEntryAsync(entry);

            var queue = await _service.GetAllAsync<SyncQueue>();
            var updateEntry = Assert.Single(queue, q => q.Operation == "update");
            Assert.Equal("timeentry", updateEntry.EntityType);
            Assert.Equal(entry.SyncId, updateEntry.EntityId);
        }

        [Fact]
        public async Task DeleteTimeEntryAsync_EnqueuesDeleteSyncEntry()
        {
            var entry = new TimeEntry { TaskItemId = 1, StartTime = DateTime.UtcNow };
            await _service.SaveTimeEntryAsync(entry);

            await _service.DeleteTimeEntryAsync(entry);

            var queue = await _service.GetAllAsync<SyncQueue>();
            var deleteEntry = Assert.Single(queue, q => q.Operation == "delete");
            Assert.Equal("timeentry", deleteEntry.EntityType);
            Assert.Equal(entry.SyncId, deleteEntry.EntityId);
        }

        [Fact]
        public async Task Migration_BackfillsPreExistingRowsExactlyOnce()
        {
            var migrationDbPath = Path.Combine(Path.GetTempPath(), $"WorkLedgerMigrationTests_{Guid.NewGuid()}.db");
            try
            {
                // Seed a table with the pre-versioning schema (no Version/OriginDeviceId columns)
                // to simulate a database created before this change existed.
                var seedConnection = new SQLiteAsyncConnection(migrationDbPath);
                await seedConnection.ExecuteAsync(
                    "CREATE TABLE Project (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT, ProjectCode TEXT, Description TEXT, CreatedAt TEXT, UpdatedAt TEXT)");
                await seedConnection.ExecuteAsync(
                    "INSERT INTO Project (Name, CreatedAt) VALUES (?, ?)", "Legacy Project", DateTime.UtcNow);
                await seedConnection.CloseAsync();

                var migrated = new LocalDbService(migrationDbPath, "device-migrated");
                await migrated.Initialization;

                var projects = await migrated.GetProjectsAsync();
                var legacy = Assert.Single(projects);
                Assert.Equal(1, legacy.Version);
                Assert.Equal("device-migrated", legacy.OriginDeviceId);
                Assert.NotEqual(Guid.Empty, legacy.SyncId);

                // Reopening the now-migrated database must not touch already-migrated rows
                // (e.g. must not bump Version to 2, must not regenerate SyncId). Re-run against
                // the same open connection path rather than closing first, since sqlite-net-pcl
                // pools connections by path.
                var reopened = new LocalDbService(migrationDbPath, "device-migrated");
                await reopened.Initialization;

                var afterReopen = await reopened.GetProjectsAsync();
                var stillMigrated = Assert.Single(afterReopen);
                Assert.Equal(1, stillMigrated.Version);
                Assert.Equal(legacy.SyncId, stillMigrated.SyncId);

                await migrated.Connection.CloseAsync();
                await reopened.Connection.CloseAsync();
            }
            finally
            {
                TryDeleteWithRetries(migrationDbPath);
            }
        }
    }
}
