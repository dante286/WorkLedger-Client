using System;
using System.IO;
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

                // Reopening the now-migrated database must not touch already-migrated rows
                // (e.g. must not bump Version to 2). Re-run against the same open connection
                // path rather than closing first, since sqlite-net-pcl pools connections by path.
                var reopened = new LocalDbService(migrationDbPath, "device-migrated");
                await reopened.Initialization;

                var afterReopen = await reopened.GetProjectsAsync();
                var stillMigrated = Assert.Single(afterReopen);
                Assert.Equal(1, stillMigrated.Version);

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
