using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkLedger.Contracts.Payloads;
using WorkLedger.Contracts.Sync;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using Xunit;

namespace WorkLedger.Tests
{
    // Cheap tests that stop cross-repo drift. Sync/Contracts/ is a vendored copy of the
    // server's WorkLedger.Contracts, so nothing but these assertions makes a rename or a
    // shape change on either side fail the build rather than silently corrupt sync.
    public class SyncContractGuardTests : IAsyncLifetime
    {
        private const string TestDeviceId = "contract-guard-device";

        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"WorkLedgerContractGuard_{Guid.NewGuid()}.db");
        private LocalDbService _db = null!;

        public async Task InitializeAsync()
        {
            _db = new LocalDbService(_dbPath, TestDeviceId);
            await _db.Initialization;
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

        // The strings the client already wrote into every existing SyncQueue row - and into
        // every file FileSyncProvider has already written - are what these mean. Renaming
        // either side stops matching that history, so it has to break the build here.
        [Fact]
        public void EntityTypeConstants_MatchTheServerContract()
        {
            Assert.Equal(SyncEntityTypes.Project, LocalDbService.EntityTypes.Project);
            Assert.Equal(SyncEntityTypes.Task, LocalDbService.EntityTypes.Task);
            Assert.Equal(SyncEntityTypes.TimeEntry, LocalDbService.EntityTypes.TimeEntry);
        }

        [Fact]
        public void OperationConstants_MatchTheServerContract()
        {
            Assert.Equal(SyncOperations.Create, LocalDbService.SyncOperations.Create);
            Assert.Equal(SyncOperations.Update, LocalDbService.SyncOperations.Update);
            Assert.Equal(SyncOperations.Delete, LocalDbService.SyncOperations.Delete);
        }

        // The test that would have caught WL-91: a payload the real queue-building path
        // produced, read through the server's DTO, with *every* field required to survive.
        // A missing property doesn't throw - it lands as the DTO's default - so asserting
        // completeness is the only way this is caught before it reaches the server.
        [Fact]
        public async Task QueuedProjectPayload_DeserializesIntoTheContractDto_WithEveryFieldPopulated()
        {
            var project = new Project
            {
                Name = "Contract Project",
                ProjectCode = "CP-1",
                Description = "Every field set on purpose",
                UpdatedAt = DateTime.UtcNow
            };
            await _db.SaveProjectAsync(project);

            var payload = await ReadQueuedPayloadAsync<ProjectPayload>(LocalDbService.EntityTypes.Project);

            Assert.Equal(project.SyncId, payload.SyncId);
            Assert.Equal("Contract Project", payload.Name);
            Assert.Equal("CP-1", payload.ProjectCode);
            Assert.Equal("Every field set on purpose", payload.Description);
            Assert.NotEqual(default, payload.CreatedAt);
            Assert.NotNull(payload.UpdatedAt);
            Assert.Equal(project.Version, payload.Version);
            Assert.Equal(TestDeviceId, payload.OriginDeviceId);
        }

        [Fact]
        public async Task QueuedTaskPayload_DeserializesIntoTheContractDto_WithEveryFieldPopulated()
        {
            var project = new Project { Name = "Parent" };
            await _db.SaveProjectAsync(project);

            var task = new TaskItem
            {
                ProjectId = project.Id,
                Name = "Contract Task",
                Notes = "Every field set on purpose",
                IsCompleted = true,
                IsArchived = true,
                UpdatedAt = DateTime.UtcNow
            };
            await _db.SaveTaskAsync(task);

            var payload = await ReadQueuedPayloadAsync<TaskItemPayload>(LocalDbService.EntityTypes.Task);

            Assert.Equal(task.SyncId, payload.SyncId);
            // WL-91 in one assertion: without the parent SyncId this is Guid.Empty and the
            // server rejects the item as an orphan.
            Assert.Equal(project.SyncId, payload.ProjectSyncId);
            Assert.NotEqual(Guid.Empty, payload.ProjectSyncId);
            Assert.Equal("Contract Task", payload.Name);
            Assert.Equal("Every field set on purpose", payload.Notes);
            Assert.True(payload.IsCompleted);
            Assert.True(payload.IsArchived);
            Assert.NotEqual(default, payload.CreatedAt);
            Assert.NotNull(payload.UpdatedAt);
            Assert.Equal(task.Version, payload.Version);
            Assert.Equal(TestDeviceId, payload.OriginDeviceId);
        }

        [Fact]
        public async Task QueuedTimeEntryPayload_DeserializesIntoTheContractDto_WithEveryFieldPopulated()
        {
            var project = new Project { Name = "Parent" };
            await _db.SaveProjectAsync(project);
            var task = new TaskItem { ProjectId = project.Id, Name = "Parent task" };
            await _db.SaveTaskAsync(task);

            var start = DateTime.UtcNow.AddHours(-1);
            var entry = new TimeEntry
            {
                TaskItemId = task.Id,
                StartTime = start,
                EndTime = start.AddMinutes(30),
                DurationSeconds = 1800,
                UpdatedAt = DateTime.UtcNow
            };
            await _db.SaveTimeEntryAsync(entry);

            var payload = await ReadQueuedPayloadAsync<TimeEntryPayload>(LocalDbService.EntityTypes.TimeEntry);

            Assert.Equal(entry.SyncId, payload.SyncId);
            Assert.Equal(task.SyncId, payload.TaskSyncId);
            Assert.NotEqual(Guid.Empty, payload.TaskSyncId);
            Assert.NotEqual(default, payload.StartTime);
            Assert.NotNull(payload.EndTime);
            Assert.Equal(1800, payload.DurationSeconds);
            Assert.NotEqual(default, payload.CreatedAt);
            Assert.NotNull(payload.UpdatedAt);
            Assert.Equal(entry.Version, payload.Version);
            Assert.Equal(TestDeviceId, payload.OriginDeviceId);
        }

        // SyncOrchestrator reads payloads with a bare JsonSerializer.Deserialize<T> and
        // default, case-SENSITIVE options. A camelCase payload therefore deserializes into
        // an all-defaults entity without throwing - silent data loss. This pins the casing
        // on both halves: what SyncJson.Options emits, and what the orchestrator can read.
        [Fact]
        public void PayloadJson_IsPascalCase_AndSurvivesTheOrchestratorsDefaultOptionsReader()
        {
            var project = new Project
            {
                Name = "Casing Check",
                ProjectCode = "CC-1",
                Version = 7,
                OriginDeviceId = TestDeviceId,
                SyncId = Guid.NewGuid()
            };

            var json = JsonSerializer.Serialize(project, SyncJson.Options);

            Assert.Contains("\"Name\"", json);
            Assert.Contains("\"OriginDeviceId\"", json);
            Assert.DoesNotContain("\"name\"", json);
            Assert.DoesNotContain("\"originDeviceId\"", json);

            // Exactly how SyncOrchestrator.DeserializePayload reads it - no options at all.
            var roundTripped = JsonSerializer.Deserialize<Project>(json);
            Assert.NotNull(roundTripped);
            Assert.Equal("Casing Check", roundTripped!.Name);
            Assert.Equal(7, roundTripped.Version);
            Assert.Equal(TestDeviceId, roundTripped.OriginDeviceId);
            Assert.Equal(project.SyncId, roundTripped.SyncId);
        }

        // The reverse direction: a payload the server sent using its own SyncJson.Options
        // has to be readable by the client's entity types.
        [Fact]
        public void ContractPayloadDto_SerializedByTheServersPolicy_ReadsBackIntoTheClientEntity()
        {
            var payload = new TaskItemPayload(
                SyncId: Guid.NewGuid(),
                ProjectSyncId: Guid.NewGuid(),
                Name: "From the server",
                Notes: "Server notes",
                IsCompleted: true,
                IsArchived: false,
                CreatedAt: DateTime.UtcNow,
                UpdatedAt: DateTime.UtcNow,
                Version: 4,
                OriginDeviceId: "server-side-device");

            var json = JsonSerializer.Serialize(payload, SyncJson.Options);
            var task = JsonSerializer.Deserialize<TaskItem>(json);

            Assert.NotNull(task);
            Assert.Equal(payload.SyncId, task!.SyncId);
            Assert.Equal(payload.ProjectSyncId, task.ProjectSyncId);
            Assert.Equal("From the server", task.Name);
            Assert.Equal("Server notes", task.Notes);
            Assert.True(task.IsCompleted);
            Assert.False(task.IsArchived);
            Assert.Equal(4, task.Version);
            Assert.Equal("server-side-device", task.OriginDeviceId);
        }

        private async Task<T> ReadQueuedPayloadAsync<T>(string entityType)
        {
            var queue = await _db.GetAllAsync<SyncQueue>();
            var entry = Assert.Single(queue, q => q.EntityType == entityType);
            Assert.NotNull(entry.PayloadJson);

            var payload = JsonSerializer.Deserialize<T>(entry.PayloadJson!, SyncJson.Options);
            Assert.NotNull(payload);
            return payload!;
        }
    }
}
