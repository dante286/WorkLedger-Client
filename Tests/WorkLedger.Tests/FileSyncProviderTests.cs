using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using WorkLedger_Client.Sync;
using Xunit;

namespace WorkLedger.Tests
{
    public class FileSyncProviderTests : IDisposable
    {
        private readonly string _syncFolder = Path.Combine(Path.GetTempPath(), "WorkLedgerSyncFolderTests_" + Guid.NewGuid());

        public void Dispose()
        {
            if (Directory.Exists(_syncFolder))
                Directory.Delete(_syncFolder, recursive: true);
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

        // Version/OriginDeviceId live inside PayloadJson (on the serialized entity), not on
        // SyncQueue itself - see Models/SyncQueue.cs. This stands in for that entity payload.
        private static SyncQueue MakeQueueEntry(int id, string entityType, Guid entityId, string operation, int version, string originDeviceId)
        {
            var payload = new { Version = version, OriginDeviceId = originDeviceId, Name = "irrelevant" };
            return new SyncQueue
            {
                Id = id,
                EntityType = entityType,
                EntityId = entityId,
                Operation = operation,
                PayloadJson = JsonSerializer.Serialize(payload),
                CreatedAt = DateTime.UtcNow
            };
        }

        [Fact]
        public async Task PushAsync_WritesJsonFile_WithExpectedNamePatternAndContents()
        {
            var provider = new FileSyncProvider(_syncFolder, "device-a");
            var entityId = Guid.NewGuid();
            var queueEntry = MakeQueueEntry(42, "project", entityId, "create", 1, "device-a");

            var result = await provider.PushAsync(new[] { queueEntry });

            Assert.Equal(new[] { 42 }, result.AcceptedSyncQueueIds);
            Assert.Empty(result.Failures);

            var changesFolder = Path.Combine(_syncFolder, "changes");
            var file = Assert.Single(Directory.GetFiles(changesFolder, "*.json"));
            Assert.Matches(@"^device-a-\d{17}\.json$", Path.GetFileName(file));

            var json = await File.ReadAllTextAsync(file);
            using var doc = JsonDocument.Parse(json);
            var entry = Assert.Single(doc.RootElement.EnumerateArray());
            Assert.Equal("project", entry.GetProperty("EntityType").GetString());
            Assert.Equal(entityId, entry.GetProperty("EntityId").GetGuid());
            Assert.Equal("create", entry.GetProperty("Operation").GetString());
            Assert.Equal(1, entry.GetProperty("Version").GetInt32());
            Assert.Equal("device-a", entry.GetProperty("OriginDeviceId").GetString());
        }

        [Fact]
        public async Task PullAsync_ReturnsChanges_WrittenByAnotherDevice()
        {
            var writer = new FileSyncProvider(_syncFolder, "device-b");
            var entityId = Guid.NewGuid();
            await writer.PushAsync(new[] { MakeQueueEntry(1, "task", entityId, "update", 3, "device-b") });

            var reader = new FileSyncProvider(_syncFolder, "device-a");
            var pulled = (await reader.PullAsync(DateTime.MinValue)).Changes.ToList();

            var change = Assert.Single(pulled);
            Assert.Equal("task", change.EntityType);
            Assert.Equal(entityId, change.EntityId);
            Assert.Equal(3, change.Version);
            Assert.Equal("device-b", change.OriginDeviceId);
        }

        [Fact]
        public async Task PullAsync_ExcludesOwnDevicesChanges()
        {
            var provider = new FileSyncProvider(_syncFolder, "device-a");
            await provider.PushAsync(new[] { MakeQueueEntry(1, "project", Guid.NewGuid(), "create", 1, "device-a") });

            var pulled = (await provider.PullAsync(DateTime.MinValue)).Changes;

            Assert.Empty(pulled);
        }

        [Fact]
        public async Task PullAsync_SurfacesConflictingChanges_FromDifferentDevices_AsDistinctEntries()
        {
            var sharedEntityId = Guid.NewGuid();

            var deviceB = new FileSyncProvider(_syncFolder, "device-b");
            await deviceB.PushAsync(new[] { MakeQueueEntry(1, "project", sharedEntityId, "update", 2, "device-b") });

            var deviceC = new FileSyncProvider(_syncFolder, "device-c");
            await deviceC.PushAsync(new[] { MakeQueueEntry(1, "project", sharedEntityId, "update", 3, "device-c") });

            var reader = new FileSyncProvider(_syncFolder, "device-a");
            var pulled = (await reader.PullAsync(DateTime.MinValue)).Changes.ToList();

            // Both surfaced as distinct RemoteChanges rather than deduplicated - resolving
            // this conflict is SyncOrchestrator's job (Plan 4), not this provider's.
            Assert.Equal(2, pulled.Count);
            Assert.Contains(pulled, c => c.OriginDeviceId == "device-b" && c.Version == 2);
            Assert.Contains(pulled, c => c.OriginDeviceId == "device-c" && c.Version == 3);
        }

        [Fact]
        public async Task PushAsync_FailsGracefully_WhenSyncFolderIsUnwritable()
        {
            // A plain file where the sync folder is expected blocks Directory.CreateDirectory
            // from ever creating "<blocked>/changes" underneath it - simulates a config
            // pointing at a location that can't be turned into a folder.
            var blockedPath = Path.Combine(Path.GetTempPath(), "WorkLedgerBlockedSyncFolder_" + Guid.NewGuid());
            File.WriteAllText(blockedPath, "not a folder");
            try
            {
                var provider = new FileSyncProvider(blockedPath, "device-a");
                var queueEntry = MakeQueueEntry(7, "project", Guid.NewGuid(), "create", 1, "device-a");

                var result = await provider.PushAsync(new[] { queueEntry });

                Assert.Empty(result.AcceptedSyncQueueIds);
                var failure = Assert.Single(result.Failures);
                Assert.Equal(7, failure.SyncQueueId);
            }
            finally
            {
                TryDeleteWithRetries(blockedPath);
            }
        }

        [Fact]
        public async Task PullAsync_ReturnsEmpty_WhenSyncFolderDoesNotExist()
        {
            var provider = new FileSyncProvider(Path.Combine(_syncFolder, "never-created"), "device-a");

            var pulled = (await provider.PullAsync(DateTime.MinValue)).Changes;

            Assert.Empty(pulled);
        }

        [Fact]
        public async Task PullAsync_PrunesChangeFiles_OlderThanRetentionWindow()
        {
            var changesFolder = Path.Combine(_syncFolder, "changes");
            Directory.CreateDirectory(changesFolder);

            var oldFilePath = Path.Combine(changesFolder, $"device-a-{DateTime.UtcNow.AddDays(-2):yyyyMMddHHmmssfff}.json");
            File.WriteAllText(oldFilePath, "[]");

            var freshFilePath = Path.Combine(changesFolder, $"device-a-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
            File.WriteAllText(freshFilePath, "[]");

            var provider = new FileSyncProvider(_syncFolder, "device-a", TimeSpan.FromDays(1));

            await provider.PullAsync(DateTime.MinValue);

            Assert.False(File.Exists(oldFilePath));
            Assert.True(File.Exists(freshFilePath));
        }

        [Fact]
        public async Task PullAsync_ReturnsAnAgedOutChange_BeforePruningItAway()
        {
            var changesFolder = Path.Combine(_syncFolder, "changes");
            Directory.CreateDirectory(changesFolder);

            var entityId = Guid.NewGuid();
            var oldTimestamp = DateTime.UtcNow.AddDays(-2);
            var oldFilePath = Path.Combine(changesFolder, $"device-b-{oldTimestamp:yyyyMMddHHmmssfff}.json");
            var payload = JsonSerializer.Serialize(new[]
            {
                new
                {
                    EntityType = "project",
                    EntityId = entityId,
                    Operation = "create",
                    PayloadJson = (string?)null,
                    Version = 1,
                    OriginDeviceId = "device-b",
                    CreatedAt = oldTimestamp
                }
            });
            File.WriteAllText(oldFilePath, payload);

            var provider = new FileSyncProvider(_syncFolder, "device-a", TimeSpan.FromDays(1));

            var pulled = (await provider.PullAsync(DateTime.MinValue)).Changes.ToList();

            // A device that hasn't synced in a while still gets one last look at a change
            // during the very call that discovers it, before its file ages out.
            var change = Assert.Single(pulled);
            Assert.Equal(entityId, change.EntityId);
            Assert.False(File.Exists(oldFilePath));
        }

        [Fact]
        public async Task TwoDevices_CanPushAndPullEachOthersChanges_ThroughSharedFolder()
        {
            var dbPathA = Path.Combine(Path.GetTempPath(), $"WorkLedgerFileSyncA_{Guid.NewGuid()}.db");
            var dbPathB = Path.Combine(Path.GetTempPath(), $"WorkLedgerFileSyncB_{Guid.NewGuid()}.db");
            var dbA = new LocalDbService(dbPathA, "device-a");
            var dbB = new LocalDbService(dbPathB, "device-b");
            await dbA.Initialization;
            await dbB.Initialization;

            try
            {
                var orchestratorA = new SyncOrchestrator(dbA, new FileSyncProvider(_syncFolder, "device-a"));
                var orchestratorB = new SyncOrchestrator(dbB, new FileSyncProvider(_syncFolder, "device-b"));

                var projectOnA = new Project { Name = "From A" };
                await dbA.SaveProjectAsync(projectOnA);

                var projectOnB = new Project { Name = "From B" };
                await dbB.SaveProjectAsync(projectOnB);

                await orchestratorA.RunSyncAsync(); // A pushes "From A"
                await orchestratorB.RunSyncAsync(); // B pushes "From B" and pulls A's file
                await orchestratorA.RunSyncAsync(); // A pulls B's file

                var allOnA = await dbA.GetProjectsAsync();
                var allOnB = await dbB.GetProjectsAsync();

                Assert.Equal(2, allOnA.Count);
                Assert.Equal(2, allOnB.Count);
                Assert.Contains(allOnA, p => p.Name == "From A");
                Assert.Contains(allOnA, p => p.Name == "From B");
                Assert.Contains(allOnB, p => p.Name == "From A");
                Assert.Contains(allOnB, p => p.Name == "From B");
            }
            finally
            {
                await dbA.Connection.CloseAsync();
                await dbB.Connection.CloseAsync();
                TryDeleteWithRetries(dbPathA);
                TryDeleteWithRetries(dbPathB);
            }
        }
    }
}
