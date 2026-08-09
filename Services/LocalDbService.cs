using SQLite;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Services
{
    public class LocalDbService
    {
        // Sync queue string conventions. Plan 4's orchestrator switches on these, so keep
        // call sites using these constants rather than ad-hoc literals.
        private static class EntityTypes
        {
            public const string Project = "project";
            public const string Task = "task";
            public const string TimeEntry = "timeentry";
        }

        private static class SyncOperations
        {
            public const string Create = "create";
            public const string Update = "update";
            public const string Delete = "delete";
        }

        private readonly SQLiteAsyncConnection _db;

        // Identifies which installation this instance is running as, stamped onto
        // every row this instance writes so the sync orchestrator (Plan 4) can tell
        // local writes from remote ones.
        public string? DeviceId { get; }

        // Fire-and-forget initialization is still kicked off from the constructor for
        // normal app startup, but the task is exposed so callers that need the schema
        // and migration backfill to be complete first (e.g. tests) can await it.
        public Task Initialization { get; }

        public LocalDbService(string dbPath, string? deviceId = null)
        {
            _db = new SQLiteAsyncConnection(dbPath);
            DeviceId = deviceId;
            Initialization = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            await _db.CreateTableAsync<Project>();
            await _db.CreateTableAsync<TaskItem>();
            await _db.CreateTableAsync<TimeEntry>();
            await _db.CreateTableAsync<SyncQueue>();

            await BackfillVersioningAsync<Project>();
            await BackfillVersioningAsync<TaskItem>();
            await BackfillVersioningAsync<TimeEntry>();
        }

        // sqlite-net-pcl's CreateTableAsync adds missing columns to an existing table via
        // ALTER TABLE, but leaves pre-existing rows with Version as SQL NULL rather than the
        // C# initializer's 1 (ALTER TABLE ADD COLUMN has no default here). Guard on NULL, not
        // just 0 — "Version = 0" never matches a NULL column in SQL. Backfills those rows
        // exactly once: the guard SELECT is a cheap no-write no-op on fresh installs and
        // already-migrated databases.
        private async Task BackfillVersioningAsync<T>() where T : class, IVersionedEntity, new()
        {
            var tableName = typeof(T).Name;
            var unmigratedCount = await _db.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {tableName} WHERE Version IS NULL OR Version = 0");

            if (unmigratedCount == 0)
                return;

            await _db.ExecuteAsync(
                $"UPDATE {tableName} SET Version = 1, OriginDeviceId = ? WHERE Version IS NULL OR Version = 0",
                DeviceId);
        }

        private void StampForInsert(IVersionedEntity entity)
        {
            if (entity.Version <= 0)
                entity.Version = 1;

            entity.OriginDeviceId = DeviceId;
        }

        private void StampForUpdate(IVersionedEntity entity)
        {
            entity.Version++;
            entity.OriginDeviceId = DeviceId;
        }

        // Builds the SyncQueue row queued alongside every entity mutation. Payload is the
        // entity itself (post-stamping) so Plan 4's orchestrator has Id/Version/OriginDeviceId
        // to reconcile against, including for deletes (a null payload can't distinguish
        // "row doesn't exist" from "no data available").
        private static SyncQueue BuildSyncQueueEntry(string entityType, int entityId, string operation, object? payload)
        {
            return new SyncQueue
            {
                EntityType = entityType,
                EntityId = entityId,
                Operation = operation,
                PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload),
                IsSynced = false
            };
        }

        // Generic helpers
        public Task<int> InsertAsync<T>(T item) where T : new()
            => _db.InsertAsync(item);

        public Task<int> SaveAsync<T>(T entity) where T : new()
        => _db.InsertOrReplaceAsync(entity);

        public Task<int> UpdateAsync<T>(T item) where T : new()
            => _db.UpdateAsync(item);

        public Task<int> DeleteAsync<T>(T item) where T : new()
            => _db.DeleteAsync(item);

        public Task<List<T>> GetAllAsync<T>() where T : new()
            => _db.Table<T>().ToListAsync();

        public Task<T> GetByIdAsync<T>(int id) where T : new()
            => _db.FindAsync<T>(id);

        // Project-specific methods
        public Task<List<Project>> GetProjectsAsync()
        {
            return _db.Table<Project>()
                      .OrderBy(p => p.Name)
                      .ToListAsync();
        }

        public async Task<Project?> GetProjectByIdAsync(int id)
        {
            return await _db.Table<Project>()
                            .Where(p => p.Id == id)
                            .FirstOrDefaultAsync();
        }

        public async Task<int> SaveProjectAsync(Project project)
        {
            var isInsert = project.Id == 0;
            if (isInsert)
                StampForInsert(project);
            else
                StampForUpdate(project);

            var rowsAffected = 0;
            await _db.RunInTransactionAsync(conn =>
            {
                rowsAffected = isInsert ? conn.Insert(project) : conn.Update(project);
                var operation = isInsert ? SyncOperations.Create : SyncOperations.Update;
                conn.Insert(BuildSyncQueueEntry(EntityTypes.Project, project.Id, operation, project));
            });
            return rowsAffected;
        }

        public async Task<int> DeleteProjectAsync(Project project)
        {
            var rowsAffected = 0;
            await _db.RunInTransactionAsync(conn =>
            {
                rowsAffected = conn.Delete(project);
                conn.Insert(BuildSyncQueueEntry(EntityTypes.Project, project.Id, SyncOperations.Delete, project));
            });
            return rowsAffected;
        }

        // TaskItem-specific methods
        public Task<List<TaskItem>> GetTasksByProjectIdAsync(int projectId)
        {
            return _db.Table<TaskItem>()
                      .Where(t => t.ProjectId == projectId)
                      .OrderBy(t => t.IsCompleted)
                      .ThenBy(t => t.CreatedAt)
                      .ToListAsync();
        }

        public async Task<int> SaveTaskAsync(TaskItem task)
        {
            var isInsert = task.Id == 0;
            if (isInsert)
                StampForInsert(task);
            else
                StampForUpdate(task);

            var rowsAffected = 0;
            await _db.RunInTransactionAsync(conn =>
            {
                rowsAffected = isInsert ? conn.Insert(task) : conn.Update(task);
                var operation = isInsert ? SyncOperations.Create : SyncOperations.Update;
                conn.Insert(BuildSyncQueueEntry(EntityTypes.Task, task.Id, operation, task));
            });
            return rowsAffected;
        }

        public async Task<int> DeleteTaskAsync(TaskItem task)
        {
            var rowsAffected = 0;
            await _db.RunInTransactionAsync(conn =>
            {
                rowsAffected = conn.Delete(task);
                conn.Insert(BuildSyncQueueEntry(EntityTypes.Task, task.Id, SyncOperations.Delete, task));
            });
            return rowsAffected;
        }

        // TimeEntry-specific methods
        public Task<List<TimeEntry>> GetTimeEntriesByTaskIdAsync(int taskId)
        {
            return _db.Table<TimeEntry>()
                      .Where(te => te.TaskItemId == taskId)
                      .OrderByDescending(te => te.StartTime)
                      .ToListAsync();
        }

        public async Task<int> SaveTimeEntryAsync(TimeEntry entry)
        {
            var isInsert = entry.Id == 0;
            if (isInsert)
                StampForInsert(entry);
            else
                StampForUpdate(entry);

            var rowsAffected = 0;
            await _db.RunInTransactionAsync(conn =>
            {
                rowsAffected = isInsert ? conn.Insert(entry) : conn.Update(entry);
                var operation = isInsert ? SyncOperations.Create : SyncOperations.Update;
                conn.Insert(BuildSyncQueueEntry(EntityTypes.TimeEntry, entry.Id, operation, entry));
            });
            return rowsAffected;
        }

        public async Task<int> DeleteTimeEntryAsync(TimeEntry entry)
        {
            var rowsAffected = 0;
            await _db.RunInTransactionAsync(conn =>
            {
                rowsAffected = conn.Delete(entry);
                conn.Insert(BuildSyncQueueEntry(EntityTypes.TimeEntry, entry.Id, SyncOperations.Delete, entry));
            });
            return rowsAffected;
        }


        // Expose raw connection if needed
        public SQLiteAsyncConnection Connection => _db;

    }
}
