using System;
using System.Collections.Generic;
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
    // Fake ISyncProvider whose Push/Pull results are set by each test rather than
    // driven by a real transport.
    public class FakeSyncProvider : ISyncProvider
    {
        public SyncResult PushResult { get; set; } = new SyncResult();
        public List<RemoteChange> RemoteChanges { get; set; } = new();
        public List<SyncQueue> ReceivedPush { get; } = new();

        public Task<SyncResult> PushAsync(IEnumerable<SyncQueue> pendingChanges)
        {
            ReceivedPush.AddRange(pendingChanges);
            return Task.FromResult(PushResult);
        }

        public Task<IEnumerable<RemoteChange>> PullAsync(DateTime since)
            => Task.FromResult<IEnumerable<RemoteChange>>(RemoteChanges);
    }

    public class SyncOrchestratorTests : IAsyncLifetime
    {
        private const string LocalDeviceId = "local-device";
        private const string RemoteDeviceId = "remote-device";

        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"WorkLedgerSyncTests_{Guid.NewGuid()}.db");
        private LocalDbService _db = null!;
        private FakeSyncProvider _provider = null!;
        private SyncOrchestrator _orchestrator = null!;

        public async Task InitializeAsync()
        {
            _db = new LocalDbService(_dbPath, LocalDeviceId);
            await _db.Initialization;
            _provider = new FakeSyncProvider();
            _orchestrator = new SyncOrchestrator(_db, _provider);
        }

        public async Task DisposeAsync()
        {
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

        private static string ToPayload(object entity) => JsonSerializer.Serialize(entity);

        [Fact]
        public async Task RunSyncAsync_PushesPendingRows_ThenPrunesThemOncePushed()
        {
            var project = new Project { Name = "New Project" };
            await _db.SaveProjectAsync(project);

            var pendingBefore = await _db.GetAllAsync<SyncQueue>();
            var queuedId = Assert.Single(pendingBefore).Id;
            _provider.PushResult = new SyncResult { AcceptedSyncQueueIds = new List<int> { queuedId } };

            await _orchestrator.RunSyncAsync();

            // WL-60: an accepted row is marked synced and then pruned within the same
            // sync cycle, rather than lingering in the table forever.
            Assert.Single(_provider.ReceivedPush);
            var queueAfter = await _db.GetAllAsync<SyncQueue>();
            Assert.Empty(queueAfter);
        }

        [Fact]
        public async Task RunSyncAsync_LeavesRowsUnsynced_WhenProviderDoesNotAcceptThem()
        {
            var project = new Project { Name = "New Project" };
            await _db.SaveProjectAsync(project);

            _provider.PushResult = new SyncResult();

            await _orchestrator.RunSyncAsync();

            // Not accepted -> not synced -> must survive pruning so it's retried later.
            var queueAfter = await _db.GetAllAsync<SyncQueue>();
            Assert.False(Assert.Single(queueAfter).IsSynced);
        }

        [Fact]
        public async Task RunSyncAsync_RemoteChangeWithHigherVersion_OverwritesLocalRow()
        {
            var project = new Project { Name = "Local Name" };
            await _db.SaveProjectAsync(project);
            Assert.Equal(1, project.Version);

            var remoteProject = new Project { SyncId = project.SyncId, Name = "Remote Name", Version = 5 };
            _provider.RemoteChanges.Add(new RemoteChange
            {
                EntityType = "project",
                EntityId = project.SyncId,
                Operation = "update",
                PayloadJson = ToPayload(remoteProject),
                Version = 5,
                OriginDeviceId = RemoteDeviceId
            });

            await _orchestrator.RunSyncAsync();

            var stored = await _db.GetProjectByIdAsync(project.Id);
            Assert.NotNull(stored);
            Assert.Equal("Remote Name", stored!.Name);
            Assert.True(stored.Version > 5);
        }

        [Fact]
        public async Task RunSyncAsync_RemoteChangeWithLowerOrEqualVersion_IsDiscarded()
        {
            var project = new Project { Name = "Local Name" };
            await _db.SaveProjectAsync(project);
            await _db.SaveProjectAsync(project); // bump to Version 2
            Assert.Equal(2, project.Version);

            var remoteProject = new Project { SyncId = project.SyncId, Name = "Stale Remote Name", Version = 2 };
            _provider.RemoteChanges.Add(new RemoteChange
            {
                EntityType = "project",
                EntityId = project.SyncId,
                Operation = "update",
                PayloadJson = ToPayload(remoteProject),
                Version = 2, // equal to local -> local wins
                OriginDeviceId = RemoteDeviceId
            });

            await _orchestrator.RunSyncAsync();

            var stored = await _db.GetProjectByIdAsync(project.Id);
            Assert.NotNull(stored);
            Assert.Equal("Local Name", stored!.Name);
            Assert.Equal(2, stored.Version);
        }

        [Fact]
        public async Task RunSyncAsync_RemoteChangeMatchingLocalDeviceId_IsSkippedAsSelfEcho()
        {
            var project = new Project { Name = "Local Name" };
            await _db.SaveProjectAsync(project);

            var remoteProject = new Project { SyncId = project.SyncId, Name = "Echoed Name", Version = 99 };
            _provider.RemoteChanges.Add(new RemoteChange
            {
                EntityType = "project",
                EntityId = project.SyncId,
                Operation = "update",
                PayloadJson = ToPayload(remoteProject),
                Version = 99,
                OriginDeviceId = LocalDeviceId // same as this device -> self-echo
            });

            await _orchestrator.RunSyncAsync();

            var stored = await _db.GetProjectByIdAsync(project.Id);
            Assert.NotNull(stored);
            Assert.Equal("Local Name", stored!.Name);
        }

        [Fact]
        public async Task RunSyncAsync_RemoteDeleteOperation_RemovesLocalRow()
        {
            var task = new TaskItem { Name = "To Delete", ProjectId = 1 };
            await _db.SaveTaskAsync(task);

            _provider.RemoteChanges.Add(new RemoteChange
            {
                EntityType = "task",
                EntityId = task.SyncId,
                Operation = "delete",
                Version = task.Version + 1,
                OriginDeviceId = RemoteDeviceId
            });

            await _orchestrator.RunSyncAsync();

            var stored = await _db.GetByIdAsync<TaskItem>(task.Id);
            Assert.Null(stored);
        }

        // WL-56: two devices offline from each other independently create a row and, since
        // autoincrement counters are per-device, both land on local Id = 1. Merging device B's
        // create into device A must not collide with (overwrite/skip) device A's own row -
        // SyncId, not local Id, is what the orchestrator uses to recognize "same entity".
        [Fact]
        public async Task RunSyncAsync_TwoDevicesWithCollidingLocalIds_DoNotCollideAfterSync()
        {
            var dbPathB = Path.Combine(Path.GetTempPath(), $"WorkLedgerSyncTests_{Guid.NewGuid()}.db");
            var dbB = new LocalDbService(dbPathB, "device-b");
            await dbB.Initialization;
            try
            {
                var projectA = new Project { Name = "From A" };
                await _db.SaveProjectAsync(projectA); // local Id = 1 on device A

                var projectB = new Project { Name = "From B" };
                await dbB.SaveProjectAsync(projectB); // local Id = 1 on device B too - colliding

                Assert.Equal(1, projectA.Id);
                Assert.Equal(1, projectB.Id);
                Assert.NotEqual(projectA.SyncId, projectB.SyncId);

                // Simulate device A pulling device B's create.
                _provider.RemoteChanges.Add(new RemoteChange
                {
                    EntityType = "project",
                    EntityId = projectB.SyncId,
                    Operation = "create",
                    PayloadJson = ToPayload(projectB),
                    Version = projectB.Version,
                    OriginDeviceId = "device-b"
                });

                await _orchestrator.RunSyncAsync();

                var allOnA = await _db.GetProjectsAsync();
                Assert.Equal(2, allOnA.Count);
                Assert.Contains(allOnA, p => p.SyncId == projectA.SyncId && p.Name == "From A");
                Assert.Contains(allOnA, p => p.SyncId == projectB.SyncId && p.Name == "From B");
            }
            finally
            {
                await dbB.Connection.CloseAsync();
                TryDeleteWithRetries(dbPathB);
            }
        }

        // WL-93: the regression the ticket describes, driven end to end off device B's real
        // SyncQueue rows rather than hand-built RemoteChanges - the bug lived in what
        // LocalDbService queued, so a test that hand-writes the version would have passed
        // against the broken code.
        [Fact]
        public async Task RunSyncAsync_DeleteFromAnInSyncPeer_RemovesTheLocalRow()
        {
            var dbPathB = Path.Combine(Path.GetTempPath(), $"WorkLedgerSyncTests_{Guid.NewGuid()}.db");
            var dbB = new LocalDbService(dbPathB, RemoteDeviceId);
            await dbB.Initialization;
            try
            {
                // B creates a project and A pulls it, leaving both devices in sync at Version 1.
                var projectB = new Project { Name = "Shared" };
                await dbB.SaveProjectAsync(projectB);
                _provider.RemoteChanges.AddRange(await ToRemoteChangesAsync(dbB));
                await _orchestrator.RunSyncAsync();

                var onA = Assert.Single(await _db.GetProjectsAsync());
                Assert.Equal(projectB.SyncId, onA.SyncId);
                Assert.Equal(projectB.Version, onA.Version);

                // B deletes it. Before WL-93 the queued tombstone carried Version 1 - the same
                // version A already holds - so RemoteWins' strict > dropped it on arrival.
                await dbB.Connection.DeleteAllAsync<SyncQueue>();
                await dbB.DeleteProjectAsync(projectB);

                _provider.RemoteChanges.Clear();
                _provider.RemoteChanges.AddRange(await ToRemoteChangesAsync(dbB));
                await _orchestrator.RunSyncAsync();

                Assert.Empty(await _db.GetProjectsAsync());
            }
            finally
            {
                await dbB.Connection.CloseAsync();
                TryDeleteWithRetries(dbPathB);
            }
        }

        // Mirrors FileSyncProvider.ToWireEntry: SyncQueue carries no Version/OriginDeviceId
        // of its own, so both are lifted out of the serialized payload.
        private static async Task<List<RemoteChange>> ToRemoteChangesAsync(LocalDbService db)
        {
            var queued = await db.GetAllAsync<SyncQueue>();
            return queued.Select(entry =>
            {
                var probe = JsonSerializer.Deserialize<VersionProbe>(entry.PayloadJson!);
                return new RemoteChange
                {
                    EntityType = entry.EntityType ?? string.Empty,
                    EntityId = entry.EntityId,
                    Operation = entry.Operation ?? string.Empty,
                    PayloadJson = entry.PayloadJson,
                    Version = probe?.Version ?? 0,
                    OriginDeviceId = probe?.OriginDeviceId
                };
            }).ToList();
        }

        private class VersionProbe
        {
            public int Version { get; set; }
            public string? OriginDeviceId { get; set; }
        }
    }
}
