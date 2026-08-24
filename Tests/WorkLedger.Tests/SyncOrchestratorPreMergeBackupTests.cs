using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using WorkLedger_Client.Sync;
using Xunit;

namespace WorkLedger.Tests
{
    // WL-51 (plan 7 item 6): the merge has no undo, so a safety backup is taken immediately
    // before the first remote change of a run overwrites or deletes local data. The rules the
    // hook has to obey are all about *when* it fires, which is what these cover.
    public class SyncOrchestratorPreMergeBackupTests : IAsyncLifetime
    {
        private const string LocalDeviceId = "local-device";
        private const string RemoteDeviceId = "remote-device";

        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"WorkLedgerPreMergeTests_{Guid.NewGuid()}.db");
        private LocalDbService _db = null!;
        private FakeSyncProvider _provider = null!;

        // Every project name the database held at the moment a backup was requested. Proves
        // the hook runs *before* the overwrite, not merely somewhere during the run.
        private readonly List<string?> _namesAtBackupTime = new();

        private SyncOrchestrator _orchestrator = null!;

        public async Task InitializeAsync()
        {
            _db = new LocalDbService(_dbPath, LocalDeviceId);
            await _db.Initialization;
            _provider = new FakeSyncProvider();
            _orchestrator = new SyncOrchestrator(_db, _provider, cursorStore: null, preMergeBackupAsync: RecordBackupAsync);
        }

        public async Task DisposeAsync()
        {
            await _db.Connection.CloseAsync();
            TryDeleteWithRetries(_dbPath);
        }

        private async Task RecordBackupAsync()
        {
            var rows = await _db.GetAllAsync<Project>();
            _namesAtBackupTime.Add(rows.Count == 0 ? null : rows[0].Name);
        }

        [Fact]
        public async Task RunSyncAsync_RemoteWinsOverwritingALocalRow_BacksUpBeforeApplyingIt()
        {
            var local = new Project { Name = "Local name" };
            await _db.SaveProjectAsync(local);

            _provider.RemoteChanges = new List<RemoteChange>
            {
                ProjectChange(local.SyncId, "Remote name", version: local.Version + 1)
            };

            await _orchestrator.RunSyncAsync();

            var backedUpName = Assert.Single(_namesAtBackupTime);
            Assert.Equal("Local name", backedUpName);

            var merged = Assert.Single(await _db.GetAllAsync<Project>());
            Assert.Equal("Remote name", merged.Name);
        }

        [Fact]
        public async Task RunSyncAsync_RemoteDeleteOfALocalRow_BacksUpFirst()
        {
            var local = new Project { Name = "Local name" };
            await _db.SaveProjectAsync(local);

            _provider.RemoteChanges = new List<RemoteChange>
            {
                new()
                {
                    EntityType = "project",
                    EntityId = local.SyncId,
                    Operation = "delete",
                    Version = local.Version + 1,
                    OriginDeviceId = RemoteDeviceId,
                    PayloadJson = JsonSerializer.Serialize(local)
                }
            };

            await _orchestrator.RunSyncAsync();

            Assert.Single(_namesAtBackupTime);
            Assert.Empty(await _db.GetAllAsync<Project>());
        }

        [Fact]
        public async Task RunSyncAsync_SeveralRemoteWinsInOneRun_BacksUpOnlyOnce()
        {
            // A run applies zero of these most of the time and a handful at once when it does;
            // copying the database once per overwrite would cost far more than it protects.
            var first = new Project { Name = "First" };
            var second = new Project { Name = "Second" };
            await _db.SaveProjectAsync(first);
            await _db.SaveProjectAsync(second);

            _provider.RemoteChanges = new List<RemoteChange>
            {
                ProjectChange(first.SyncId, "First updated", first.Version + 1),
                ProjectChange(second.SyncId, "Second updated", second.Version + 1)
            };

            await _orchestrator.RunSyncAsync();

            Assert.Single(_namesAtBackupTime);
        }

        [Fact]
        public async Task RunSyncAsync_RemoteInsertOfAnUnknownEntity_DoesNotBackUp()
        {
            // An entity this device has never seen destroys nothing on arrival, so there is
            // nothing to snapshot.
            _provider.RemoteChanges = new List<RemoteChange>
            {
                ProjectChange(Guid.NewGuid(), "Brand new", version: 1)
            };

            await _orchestrator.RunSyncAsync();

            Assert.Empty(_namesAtBackupTime);
            Assert.Single(await _db.GetAllAsync<Project>());
        }

        [Fact]
        public async Task RunSyncAsync_RemoteChangeThatLosesTheVersionComparison_DoesNotBackUp()
        {
            var local = new Project { Name = "Local name" };
            await _db.SaveProjectAsync(local);

            // Same version: local wins, nothing is written, nothing is at risk.
            _provider.RemoteChanges = new List<RemoteChange>
            {
                ProjectChange(local.SyncId, "Stale remote", version: local.Version)
            };

            await _orchestrator.RunSyncAsync();

            Assert.Empty(_namesAtBackupTime);
            Assert.Equal("Local name", Assert.Single(await _db.GetAllAsync<Project>()).Name);
        }

        [Fact]
        public async Task RunSyncAsync_CalledTwice_BacksUpOncePerRun()
        {
            // "Once" is scoped to the run, not the orchestrator's lifetime - the per-session
            // gate lives in the caller's delegate (SyncCoordinator), not here.
            var local = new Project { Name = "Local name" };
            await _db.SaveProjectAsync(local);

            _provider.RemoteChanges = new List<RemoteChange>
            {
                ProjectChange(local.SyncId, "Second version", version: local.Version + 1)
            };
            await _orchestrator.RunSyncAsync();

            _provider.RemoteChanges = new List<RemoteChange>
            {
                ProjectChange(local.SyncId, "Third version", version: local.Version + 2)
            };
            await _orchestrator.RunSyncAsync();

            Assert.Equal(2, _namesAtBackupTime.Count);
            Assert.Equal(new[] { "Local name", "Second version" }, _namesAtBackupTime);
        }

        [Fact]
        public async Task RunSyncAsync_BackupThatThrows_DoesNotAbortTheMerge()
        {
            // Refusing to sync because the backup folder is unwritable is a worse outcome than
            // the one being guarded against.
            var orchestrator = new SyncOrchestrator(
                _db, _provider, cursorStore: null,
                preMergeBackupAsync: () => throw new IOException("backup target is unwritable"));

            var local = new Project { Name = "Local name" };
            await _db.SaveProjectAsync(local);

            _provider.RemoteChanges = new List<RemoteChange>
            {
                ProjectChange(local.SyncId, "Remote name", local.Version + 1)
            };

            var result = await orchestrator.RunSyncAsync();

            Assert.True(result.Ran);
            Assert.Equal("Remote name", Assert.Single(await _db.GetAllAsync<Project>()).Name);
        }

        // WL-101: a winner adopted from a rejected push overwrites local data just as a pulled
        // change does, so it has to reach the same safety net. It does because it travels the
        // same merge path - this is what pins that.
        [Fact]
        public async Task RunSyncAsync_AdoptedServerWinsUpdate_BacksUpBeforeApplyingIt()
        {
            var local = new Project { Name = "Local name" };
            await _db.SaveProjectAsync(local);
            var queuedId = (await _db.GetAllAsync<SyncQueue>())[0].Id;

            _provider.PushResult = new SyncResult
            {
                RejectedSyncQueueIds = new List<int> { queuedId },
                AuthoritativeChanges = new List<RemoteChange>
                {
                    ProjectChange(local.SyncId, "Remote name", local.Version + 1)
                }
            };

            await _orchestrator.RunSyncAsync();

            Assert.Equal("Local name", Assert.Single(_namesAtBackupTime));
            Assert.Equal("Remote name", Assert.Single(await _db.GetAllAsync<Project>()).Name);
        }

        private static RemoteChange ProjectChange(Guid syncId, string name, int version) => new()
        {
            EntityType = "project",
            EntityId = syncId,
            Operation = "update",
            Version = version,
            OriginDeviceId = RemoteDeviceId,
            PayloadJson = JsonSerializer.Serialize(new Project
            {
                Name = name,
                SyncId = syncId,
                Version = version,
                OriginDeviceId = RemoteDeviceId
            })
        };

        private static void TryDeleteWithRetries(string path, int maxAttempts = 5)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
    }
}
