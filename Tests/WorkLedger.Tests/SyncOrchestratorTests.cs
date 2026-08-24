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

        // Every `since` the orchestrator has asked for, so a test can assert on the cursor
        // without reaching into the orchestrator's state.
        public List<DateTime> ReceivedPullCursors { get; } = new();

        public DateTime ServerTime { get; set; } = DateTime.UtcNow;
        public bool PullSucceeds { get; set; } = true;

        public Task<SyncResult> PushAsync(IEnumerable<SyncQueue> pendingChanges)
        {
            ReceivedPush.AddRange(pendingChanges);
            return Task.FromResult(PushResult);
        }

        public Task<PullResult> PullAsync(DateTime since)
        {
            ReceivedPullCursors.Add(since);

            if (!PullSucceeds)
                return Task.FromResult(PullResult.Failed("simulated pull failure"));

            return Task.FromResult(new PullResult
            {
                Changes = RemoteChanges.ToList(),
                ServerTime = ServerTime
            });
        }
    }

    // Minimal ISyncCursorStore that keeps the cursor in memory, standing in for the
    // settings-backed one so the persistence behaviour can be asserted without touching disk.
    public class FakeCursorStore : ISyncCursorStore
    {
        public FakeCursorStore(DateTime? seed = null) => LastSyncedAtUtc = seed;

        public DateTime? LastSyncedAtUtc { get; private set; }

        public int SaveCount { get; private set; }

        public void Save(DateTime cursor)
        {
            LastSyncedAtUtc = cursor;
            SaveCount++;
        }
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

            // The merged row keeps the *author's* version and device, verbatim. This assertion used
            // to read `Version > 5`, which quietly accepted the merge being written through
            // SaveProjectAsync: that bumped to 6, restamped this device as the author, and queued
            // the change straight back out - see RunSyncAsync_MergingARemoteChange_DoesNotReQueueOrRestampIt.
            Assert.Equal(5, stored.Version);
            Assert.Equal(RemoteDeviceId, stored.OriginDeviceId);
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

        // WL-94: a rejection is the remote's final answer - it refused either because the
        // payload is invalid or because its own version won, and neither changes on a
        // re-push. Leaving the row pending would re-push it on every sync forever.
        [Fact]
        public async Task RunSyncAsync_RowsTheProviderRejected_AreRetiredRatherThanRetried()
        {
            await _db.SaveProjectAsync(new Project { Name = "Rejected" });
            var queuedId = Assert.Single(await _db.GetAllAsync<SyncQueue>()).Id;

            _provider.PushResult = new SyncResult
            {
                RejectedSyncQueueIds = new List<int> { queuedId },
                Failures = new List<SyncFailure>
                {
                    new() { SyncQueueId = queuedId, ErrorMessage = "Server version wins." }
                }
            };

            await _orchestrator.RunSyncAsync();

            Assert.Empty(await _db.GetAllAsync<SyncQueue>());
        }

        // The contrast case: a transport failure decided nothing, so the row has to survive.
        [Fact]
        public async Task RunSyncAsync_RowsThatOnlyFailedInTransit_StayQueuedForTheNextAttempt()
        {
            await _db.SaveProjectAsync(new Project { Name = "Unreachable" });
            var queuedId = Assert.Single(await _db.GetAllAsync<SyncQueue>()).Id;

            _provider.PushResult = new SyncResult
            {
                Failures = new List<SyncFailure>
                {
                    new() { SyncQueueId = queuedId, ErrorMessage = "Could not reach the sync server." }
                }
            };

            await _orchestrator.RunSyncAsync();

            Assert.False(Assert.Single(await _db.GetAllAsync<SyncQueue>()).IsSynced);
        }

        // WL-92: the cursor is the provider's clock, not this device's.
        [Fact]
        public async Task RunSyncAsync_AdvancesTheCursorToTheProvidersServerTime()
        {
            var serverTime = DateTime.UtcNow.AddHours(-3);
            _provider.ServerTime = serverTime;

            await _orchestrator.RunSyncAsync();
            await _orchestrator.RunSyncAsync();

            Assert.Equal(serverTime, _orchestrator.LastSyncedAt);
            // First pull starts from nothing, second asks from where the server said it was.
            Assert.Equal(new[] { DateTime.MinValue, serverTime }, _provider.ReceivedPullCursors);
        }

        // A failed pull is not an empty one: advancing past changes that were never delivered
        // would skip them permanently.
        [Fact]
        public async Task RunSyncAsync_WhenThePullFails_LeavesTheCursorWhereItWas()
        {
            _provider.ServerTime = DateTime.UtcNow;
            await _orchestrator.RunSyncAsync();
            var cursorAfterSuccess = _orchestrator.LastSyncedAt;

            _provider.PullSucceeds = false;
            await _orchestrator.RunSyncAsync();

            Assert.Equal(cursorAfterSuccess, _orchestrator.LastSyncedAt);
        }

        [Fact]
        public async Task RunSyncAsync_PersistsTheCursor_AndResumesFromItOnTheNextLaunch()
        {
            var store = new FakeCursorStore();
            var serverTime = DateTime.UtcNow.AddMinutes(-10);
            _provider.ServerTime = serverTime;

            var first = new SyncOrchestrator(_db, _provider, store);
            await first.RunSyncAsync();
            Assert.Equal(serverTime, store.LastSyncedAtUtc);

            // A fresh orchestrator over the same store stands in for an app restart. Before
            // WL-92 this reset to MinValue and re-pulled the entire remote history.
            _provider.ReceivedPullCursors.Clear();
            var afterRestart = new SyncOrchestrator(_db, _provider, store);
            Assert.Equal(serverTime, afterRestart.LastSyncedAt);

            await afterRestart.RunSyncAsync();
            Assert.Equal(serverTime, Assert.Single(_provider.ReceivedPullCursors));
        }

        // WL-101: the remote won the conflict and handed the winner back, saying it is a live
        // row. Adopting it settles the conflict in one merge - no cursor rewind, so no re-pull
        // of the account's history.
        [Fact]
        public async Task RunSyncAsync_AdoptingAServerWinsUpdate_MergesItWithoutRewindingTheCursor()
        {
            _provider.ServerTime = DateTime.UtcNow;
            await _orchestrator.RunSyncAsync();

            var local = new Project { Name = "Local name" };
            await _db.SaveProjectAsync(local);
            var queuedId = Assert.Single(await _db.GetAllAsync<SyncQueue>()).Id;

            _provider.PushResult = new SyncResult
            {
                RejectedSyncQueueIds = new List<int> { queuedId },
                AuthoritativeChanges = new List<RemoteChange> { WinningProjectChange(local.SyncId, "Winner", version: 7) }
            };

            _provider.ReceivedPullCursors.Clear();
            await _orchestrator.RunSyncAsync();

            var merged = Assert.Single(await _db.GetAllAsync<Project>());
            Assert.Equal("Winner", merged.Name);
            // WL-99: a merge keeps the author's version and authorship - it is not a local edit.
            Assert.Equal(7, merged.Version);
            Assert.Equal(RemoteDeviceId, merged.OriginDeviceId);

            // The point of the ticket: the cursor asked for the same window it would have
            // anyway, rather than DateTime.MinValue.
            Assert.Equal(_provider.ServerTime, Assert.Single(_provider.ReceivedPullCursors));
            Assert.Equal(_provider.ServerTime, _orchestrator.LastSyncedAt);

            // And nothing was re-queued: the rejected row is retired and pruned, and the merge
            // itself enqueues nothing (WL-99).
            Assert.Empty(await _db.GetAllAsync<SyncQueue>());
        }

        // The case the rewind existed for. A third device deleted the entity while this one
        // pushed a stale update, so the winner handed back is a tombstone. Adopting its payload
        // as an update would resurrect the row locally and permanently - the operation is what
        // makes adoption safe at all.
        [Fact]
        public async Task RunSyncAsync_AdoptingAServerWinsTombstone_DeletesTheRowInsteadOfResurrectingIt()
        {
            _provider.ServerTime = DateTime.UtcNow;
            await _orchestrator.RunSyncAsync();

            var local = new Project { Name = "Local name" };
            await _db.SaveProjectAsync(local);
            var queuedId = Assert.Single(await _db.GetAllAsync<SyncQueue>()).Id;

            var tombstone = WinningProjectChange(local.SyncId, "Local name", version: 7);
            tombstone.Operation = "delete";

            _provider.PushResult = new SyncResult
            {
                RejectedSyncQueueIds = new List<int> { queuedId },
                AuthoritativeChanges = new List<RemoteChange> { tombstone }
            };

            _provider.ReceivedPullCursors.Clear();
            await _orchestrator.RunSyncAsync();

            Assert.Empty(await _db.GetAllAsync<Project>());
            Assert.Equal(_provider.ServerTime, Assert.Single(_provider.ReceivedPullCursors));
            Assert.Empty(await _db.GetAllAsync<SyncQueue>());
        }

        // A batch can carry both kinds of rejection at once - one winner that states its
        // operation and one that doesn't. The self-describing one is merged; the other still
        // gets its rewind, and the merged one isn't undone by the re-pull that follows.
        [Fact]
        public async Task RunSyncAsync_MixedRejections_AdoptsWhatItCanAndStillRewindsForTheRest()
        {
            _provider.ServerTime = DateTime.UtcNow;
            await _orchestrator.RunSyncAsync();

            var local = new Project { Name = "Local name" };
            await _db.SaveProjectAsync(local);
            var queuedId = Assert.Single(await _db.GetAllAsync<SyncQueue>()).Id;

            _provider.PushResult = new SyncResult
            {
                RejectedSyncQueueIds = new List<int> { queuedId },
                AuthoritativeChanges = new List<RemoteChange> { WinningProjectChange(local.SyncId, "Winner", version: 7) },
                RequiresFullPull = true
            };

            _provider.ReceivedPullCursors.Clear();
            await _orchestrator.RunSyncAsync();

            Assert.Equal("Winner", Assert.Single(await _db.GetAllAsync<Project>()).Name);
            Assert.Equal(DateTime.MinValue, Assert.Single(_provider.ReceivedPullCursors));
        }

        // A winner this device authored is a self-echo, skipped exactly as it would be had the
        // same row arrived through a pull - the adoption path must not become a second, laxer
        // way in. Reachable when a push was applied server-side but its ack never arrived.
        [Fact]
        public async Task RunSyncAsync_AdoptedWinnerAuthoredByThisDevice_IsSkippedAsSelfEcho()
        {
            var local = new Project { Name = "Local name" };
            await _db.SaveProjectAsync(local);
            var queuedId = Assert.Single(await _db.GetAllAsync<SyncQueue>()).Id;

            var ownEcho = WinningProjectChange(local.SyncId, "Echoed back", version: 7);
            ownEcho.OriginDeviceId = LocalDeviceId;

            _provider.PushResult = new SyncResult
            {
                RejectedSyncQueueIds = new List<int> { queuedId },
                AuthoritativeChanges = new List<RemoteChange> { ownEcho }
            };

            await _orchestrator.RunSyncAsync();

            var unchanged = Assert.Single(await _db.GetAllAsync<Project>());
            Assert.Equal("Local name", unchanged.Name);
            Assert.Equal(local.Version, unchanged.Version);
        }

        // A winner the local row already beats loses the version comparison like any other
        // remote change - adoption doesn't bypass last-write-wins.
        [Fact]
        public async Task RunSyncAsync_AdoptedWinnerLosingTheVersionComparison_LeavesTheLocalRowAlone()
        {
            var local = new Project { Name = "Local name" };
            await _db.SaveProjectAsync(local);
            await _db.SaveProjectAsync(local);
            var queuedId = (await _db.GetAllAsync<SyncQueue>())[0].Id;

            _provider.PushResult = new SyncResult
            {
                RejectedSyncQueueIds = new List<int> { queuedId },
                AuthoritativeChanges = new List<RemoteChange>
                {
                    WinningProjectChange(local.SyncId, "Stale winner", version: local.Version - 1)
                }
            };

            await _orchestrator.RunSyncAsync();

            var unchanged = Assert.Single(await _db.GetAllAsync<Project>());
            Assert.Equal("Local name", unchanged.Name);
            Assert.Equal(local.Version, unchanged.Version);
        }

        // The shape ApiSyncProvider hands back for a server-wins rejection: the winner, keyed by
        // the pushed row's SyncId, at the version the server reported.
        private static RemoteChange WinningProjectChange(Guid syncId, string name, int version) => new()
        {
            EntityType = "project",
            EntityId = syncId,
            Operation = "update",
            Version = version,
            OriginDeviceId = RemoteDeviceId,
            PayloadJson = ToPayload(new Project
            {
                Name = name,
                SyncId = syncId,
                Version = version,
                OriginDeviceId = RemoteDeviceId
            })
        };

        // WL-98/WL-101: the fallback, for a winner that arrived without its operation stated. Our
        // copy is stale, and because a losing item performs no remote write, its timestamp is
        // already behind our cursor and an incremental pull would never re-deliver it. Rewinding
        // is what fetches the truth.
        [Fact]
        public async Task RunSyncAsync_WhenThePushReportsRequiresFullPull_RewindsTheCursorForThatPull()
        {
            _provider.ServerTime = DateTime.UtcNow;
            await _orchestrator.RunSyncAsync();
            Assert.NotEqual(DateTime.MinValue, _orchestrator.LastSyncedAt);

            await _db.SaveProjectAsync(new Project { Name = "Loser" });
            var queuedId = Assert.Single(await _db.GetAllAsync<SyncQueue>()).Id;
            _provider.PushResult = new SyncResult
            {
                RejectedSyncQueueIds = new List<int> { queuedId },
                RequiresFullPull = true
            };

            _provider.ReceivedPullCursors.Clear();
            await _orchestrator.RunSyncAsync();

            Assert.Equal(DateTime.MinValue, Assert.Single(_provider.ReceivedPullCursors));
            // And the cursor is back to server time afterwards, so it's one re-pull, not a
            // permanent reset.
            Assert.Equal(_provider.ServerTime, _orchestrator.LastSyncedAt);
        }

        // WL-91: the parent link is the half of entity identity that WL-47/WL-56 didn't
        // cover. Device B's task points at *B's* local project Id, which on device A belongs
        // to a different project entirely - so a merge that trusted the incoming int would
        // silently file the task under the wrong project.
        [Fact]
        public async Task RunSyncAsync_ChildFromAnotherDevice_IsRelinkedToTheLocalParentId()
        {
            var dbPathB = Path.Combine(Path.GetTempPath(), $"WorkLedgerSyncTests_{Guid.NewGuid()}.db");
            var dbB = new LocalDbService(dbPathB, RemoteDeviceId);
            await dbB.Initialization;
            try
            {
                // Give A its own projects first so the same project lands on a different
                // local Id on each device - otherwise both sides agree by coincidence.
                await _db.SaveProjectAsync(new Project { Name = "A's first" });
                await _db.SaveProjectAsync(new Project { Name = "A's second" });

                var projectB = new Project { Name = "From B" };
                await dbB.SaveProjectAsync(projectB);
                var taskB = new TaskItem { Name = "B's task", ProjectId = projectB.Id };
                await dbB.SaveTaskAsync(taskB);
                var entryB = new TimeEntry { TaskItemId = taskB.Id, StartTime = DateTime.UtcNow };
                await dbB.SaveTimeEntryAsync(entryB);

                Assert.Equal(1, projectB.Id); // B's local Id, already taken on A

                _provider.RemoteChanges.AddRange(await ToRemoteChangesAsync(dbB));
                await _orchestrator.RunSyncAsync();

                var mergedProject = Assert.Single(await _db.GetProjectsAsync(), p => p.SyncId == projectB.SyncId);
                Assert.NotEqual(projectB.Id, mergedProject.Id); // A gave it a different local Id

                var mergedTask = Assert.Single(await _db.GetAllAsync<TaskItem>());
                Assert.Equal(mergedProject.Id, mergedTask.ProjectId);
                Assert.Equal(projectB.SyncId, mergedTask.ProjectSyncId);

                var mergedEntry = Assert.Single(await _db.GetAllAsync<TimeEntry>());
                Assert.Equal(mergedTask.Id, mergedEntry.TaskItemId);
                Assert.Equal(taskB.SyncId, mergedEntry.TaskSyncId);

                // The whole point of the FK: the task is reachable from its project.
                var tasksUnderProject = await _db.GetTasksByProjectIdAsync(mergedProject.Id);
                Assert.Single(tasksUnderProject);
            }
            finally
            {
                await dbB.Connection.CloseAsync();
                TryDeleteWithRetries(dbPathB);
            }
        }

        // WL-91: a child can legitimately arrive before its parent (separate pulls, or a
        // parent whose change aged out). It's stored with FK 0, which no GetXByYIdAsync
        // query matches, so without a re-link pass it stays invisible forever.
        [Fact]
        public async Task RunSyncAsync_ChildArrivingBeforeItsParent_IsRelinkedOnTheNextSync()
        {
            var dbPathB = Path.Combine(Path.GetTempPath(), $"WorkLedgerSyncTests_{Guid.NewGuid()}.db");
            var dbB = new LocalDbService(dbPathB, RemoteDeviceId);
            await dbB.Initialization;
            try
            {
                var projectB = new Project { Name = "Late parent" };
                await dbB.SaveProjectAsync(projectB);
                var taskB = new TaskItem { Name = "Early child", ProjectId = projectB.Id };
                await dbB.SaveTaskAsync(taskB);

                var allChanges = await ToRemoteChangesAsync(dbB);

                // First sync delivers only the task.
                _provider.RemoteChanges.AddRange(allChanges.Where(c => c.EntityType == "task"));
                await _orchestrator.RunSyncAsync();

                var orphan = Assert.Single(await _db.GetAllAsync<TaskItem>());
                Assert.Equal(0, orphan.ProjectId);
                Assert.Equal(projectB.SyncId, orphan.ProjectSyncId);

                // The project shows up in a later pull.
                _provider.RemoteChanges.Clear();
                _provider.RemoteChanges.AddRange(allChanges.Where(c => c.EntityType == "project"));
                await _orchestrator.RunSyncAsync();

                var mergedProject = Assert.Single(await _db.GetProjectsAsync());
                var relinked = Assert.Single(await _db.GetAllAsync<TaskItem>());
                Assert.Equal(mergedProject.Id, relinked.ProjectId);
            }
            finally
            {
                await dbB.Connection.CloseAsync();
                TryDeleteWithRetries(dbPathB);
            }
        }

        // The re-link repairs a device-local column only. Routing it through SaveTaskAsync
        // would bump the Version and queue a sync entry, re-broadcasting a local repair to
        // every peer as though the task itself had changed.
        [Fact]
        public async Task RunSyncAsync_RelinkingAnOrphan_DoesNotQueueOrVersionTheRepair()
        {
            var dbPathB = Path.Combine(Path.GetTempPath(), $"WorkLedgerSyncTests_{Guid.NewGuid()}.db");
            var dbB = new LocalDbService(dbPathB, RemoteDeviceId);
            await dbB.Initialization;
            try
            {
                var projectB = new Project { Name = "Late parent" };
                await dbB.SaveProjectAsync(projectB);
                var taskB = new TaskItem { Name = "Early child", ProjectId = projectB.Id };
                await dbB.SaveTaskAsync(taskB);

                var allChanges = await ToRemoteChangesAsync(dbB);

                _provider.RemoteChanges.AddRange(allChanges.Where(c => c.EntityType == "task"));
                await _orchestrator.RunSyncAsync();
                var versionWhileOrphaned = Assert.Single(await _db.GetAllAsync<TaskItem>()).Version;
                var queuedTaskRowsBefore = (await _db.GetAllAsync<SyncQueue>()).Count(q => q.EntityType == "task");

                _provider.RemoteChanges.Clear();
                _provider.RemoteChanges.AddRange(allChanges.Where(c => c.EntityType == "project"));
                await _orchestrator.RunSyncAsync();

                var relinked = Assert.Single(await _db.GetAllAsync<TaskItem>());
                Assert.Equal(versionWhileOrphaned, relinked.Version);

                // Counted rather than asserted at zero so this stays a statement about the
                // re-link specifically: whatever merging the task did or didn't queue, the
                // re-link must not add to it.
                var queuedTaskRowsAfter = (await _db.GetAllAsync<SyncQueue>()).Count(q => q.EntityType == "task");
                Assert.Equal(queuedTaskRowsBefore, queuedTaskRowsAfter);
            }
            finally
            {
                await dbB.Connection.CloseAsync();
                TryDeleteWithRetries(dbPathB);
            }
        }

        // Applying a remote change is not a local edit. Written through SaveXAsync it was all
        // three: a version bump, a change of author, and a queued row heading back out.
        [Fact]
        public async Task RunSyncAsync_MergingARemoteChange_DoesNotReQueueOrRestampIt()
        {
            var remoteProject = new Project { SyncId = Guid.NewGuid(), Name = "From the peer", Version = 4 };
            _provider.RemoteChanges.Add(new RemoteChange
            {
                EntityType = "project",
                EntityId = remoteProject.SyncId,
                Operation = "update",
                PayloadJson = ToPayload(remoteProject),
                Version = 4,
                OriginDeviceId = RemoteDeviceId
            });

            await _orchestrator.RunSyncAsync();

            var stored = Assert.Single(await _db.GetAllAsync<Project>());
            Assert.Equal(4, stored.Version);
            Assert.Equal(RemoteDeviceId, stored.OriginDeviceId);
            Assert.Empty(await _db.GetAllAsync<SyncQueue>());
            Assert.Empty(_provider.ReceivedPush);
        }

        // The regression that motivated the fix. Two devices, one edit, then nothing but sync:
        // each merge used to re-broadcast the row one version higher, so the peer merged the echo,
        // bumped again, and the pair traded ever-higher versions of an unchanged row forever -
        // a push and a pull per round, indefinitely. Measured behaviour was v2 -> 3 -> 4 -> 5 -> 6.
        [Fact]
        public async Task RunSyncAsync_TwoDevicesRelayingOneEdit_ConvergeInsteadOfTradingVersions()
        {
            var dbPathB = Path.Combine(Path.GetTempPath(), $"WorkLedgerSyncTests_{Guid.NewGuid()}.db");
            var dbB = new LocalDbService(dbPathB, RemoteDeviceId);
            await dbB.Initialization;
            try
            {
                var providerB = new FakeSyncProvider();
                var orchestratorB = new SyncOrchestrator(dbB, providerB);

                // B authors a project and A merges it, leaving both devices holding the row.
                var projectB = new Project { Name = "Shared" };
                await dbB.SaveProjectAsync(projectB);
                _provider.RemoteChanges.AddRange(await ToRemoteChangesAsync(dbB));
                await _orchestrator.RunSyncAsync();
                await dbB.Connection.DeleteAllAsync<SyncQueue>();

                // B edits it once. Nothing else is authored from here on.
                projectB.Name = "Edited by B";
                await dbB.SaveProjectAsync(projectB);
                Assert.Equal(2, projectB.Version);

                for (var round = 0; round < 4; round++)
                {
                    // Each side's queue becomes the other's inbound changes, as a real transport
                    // would relay them.
                    var bToA = await ToRemoteChangesAsync(dbB);
                    var aToB = await ToRemoteChangesAsync(_db);

                    _provider.RemoteChanges.Clear();
                    _provider.RemoteChanges.AddRange(bToA);
                    providerB.RemoteChanges.Clear();
                    providerB.RemoteChanges.AddRange(aToB);

                    _provider.PushResult = AcceptAll(await _db.GetAllAsync<SyncQueue>());
                    providerB.PushResult = AcceptAll(await dbB.GetAllAsync<SyncQueue>());

                    await _orchestrator.RunSyncAsync();
                    await orchestratorB.RunSyncAsync();
                }

                var onA = Assert.Single(await _db.GetAllAsync<Project>());
                var onB = Assert.Single(await dbB.GetAllAsync<Project>());

                // Both devices settle on B's edit, at B's version, crediting B.
                Assert.Equal("Edited by B", onA.Name);
                Assert.Equal("Edited by B", onB.Name);
                Assert.Equal(2, onA.Version);
                Assert.Equal(2, onB.Version);
                Assert.Equal(RemoteDeviceId, onA.OriginDeviceId);

                // And the exchange is over: neither side is still generating work.
                Assert.Empty(await _db.GetAllAsync<SyncQueue>());
                Assert.Empty(await dbB.GetAllAsync<SyncQueue>());
            }
            finally
            {
                await dbB.Connection.CloseAsync();
                TryDeleteWithRetries(dbPathB);
            }
        }

        private static SyncResult AcceptAll(List<SyncQueue> queued)
            => new() { AcceptedSyncQueueIds = queued.Select(q => q.Id).ToList() };
    }
}
