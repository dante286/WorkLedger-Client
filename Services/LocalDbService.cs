using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Services
{
    public class LocalDbService
    {
        // Sync queue string conventions. Plan 4's orchestrator switches on these, so keep
        // call sites using these constants rather than ad-hoc literals. Internal (not
        // private) so SyncOrchestrator can reuse them instead of duplicating literals.
        internal static class EntityTypes
        {
            public const string Project = "project";
            public const string Task = "task";
            public const string TimeEntry = "timeentry";
        }

        internal static class SyncOperations
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

            await BackfillSyncIdsAsync<Project>();
            await BackfillSyncIdsAsync<TaskItem>();
            await BackfillSyncIdsAsync<TimeEntry>();

            // Strictly after BackfillSyncIdsAsync - these resolve each child's parent int
            // to the parent's SyncId, so every parent needs one first.
            await BackfillParentSyncIdsAsync("TaskItem", "ProjectSyncId", "Project", "ProjectId");
            await BackfillParentSyncIdsAsync("TimeEntry", "TaskSyncId", "TaskItem", "TaskItemId");
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

        // Unlike BackfillVersioningAsync, each row needs a distinct value, so this can't be a
        // single bulk UPDATE - each Id needing backfill gets its own UPDATE. Must detect via
        // raw SQL against the actual column, not by loading rows through the ORM and checking
        // row.SyncId == Guid.Empty: sqlite-net-pcl leaves a mapped property untouched when the
        // underlying column is NULL, so a freshly-constructed row's C# property initializer
        // (Guid.NewGuid()) would still be sitting there and never look empty. See WL-47.
        private async Task BackfillSyncIdsAsync<T>() where T : class, IVersionedEntity, new()
        {
            var tableName = typeof(T).Name;
            var idsNeedingBackfill = await _db.QueryAsync<IdOnly>($"SELECT Id FROM {tableName} WHERE SyncId IS NULL");

            foreach (var row in idsNeedingBackfill)
            {
                await _db.ExecuteAsync(
                    $"UPDATE {tableName} SET SyncId = ? WHERE Id = ?", Guid.NewGuid(), row.Id);
            }
        }

        // WL-91: fills in a child's cross-device parent link from its local parent int, for
        // rows written before the column existed. Unlike BackfillSyncIdsAsync this can be a
        // single correlated UPDATE, since the value isn't per-row-unique - it's looked up.
        //
        // A child whose parent int resolves to nothing (an orphan, or a parent that hasn't
        // synced in yet) has the subquery return NULL and is left alone, so it's retried on
        // the next launch once the parent exists. That NULL is also why the model property
        // has no Guid.NewGuid() initializer: sqlite-net-pcl leaves a mapped property at its
        // C# default when the column is NULL, and Guid.Empty is exactly the "parent unknown"
        // sentinel the re-link pass looks for. Same NULL-vs-0 caveat as the other backfills.
        private async Task BackfillParentSyncIdsAsync(
            string childTable, string parentSyncIdColumn, string parentTable, string parentIdColumn)
        {
            var unmigratedCount = await _db.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {childTable} WHERE {parentSyncIdColumn} IS NULL");

            if (unmigratedCount == 0)
                return;

            await _db.ExecuteAsync(
                $@"UPDATE {childTable}
                   SET {parentSyncIdColumn} = (
                       SELECT p.SyncId FROM {parentTable} p WHERE p.Id = {childTable}.{parentIdColumn})
                   WHERE {parentSyncIdColumn} IS NULL");
        }

        // Column-projection DTO for BackfillSyncIdsAsync's "which rows are missing a SyncId"
        // query - deliberately not one of the real entity types, since selecting only Id
        // through the ORM into e.g. Project would otherwise run into the same NULL-leaves-the-
        // constructor-default problem BackfillSyncIdsAsync exists to avoid.
        private class IdOnly
        {
            public int Id { get; set; }
        }

        private void StampForInsert(IVersionedEntity entity)
        {
            if (entity.Version <= 0)
                entity.Version = 1;

            if (entity.SyncId == Guid.Empty)
                entity.SyncId = Guid.NewGuid();

            entity.OriginDeviceId = DeviceId;
        }

        private void StampForUpdate(IVersionedEntity entity)
        {
            entity.Version++;
            entity.OriginDeviceId = DeviceId;
        }

        // WL-93: a delete has to bump the version for the same reason an update does.
        // The queued tombstone carries this entity as its payload, and every receiver
        // (SyncOrchestrator.RemoteWins, and the server's own last-write-wins) compares
        // with a strict >. Queued at the entity's current Version, an in-sync peer holds
        // the identical version, loses the comparison, and silently drops the delete -
        // so the row reappears on the next edit that re-syncs it.
        private void StampForDelete(IVersionedEntity entity) => StampForUpdate(entity);

        // Builds the SyncQueue row queued alongside every entity mutation. Payload is the
        // entity itself (post-stamping) so Plan 4's orchestrator has Id/Version/OriginDeviceId
        // to reconcile against, including for deletes (a null payload can't distinguish
        // "row doesn't exist" from "no data available").
        private static SyncQueue BuildSyncQueueEntry(string entityType, Guid entityId, string operation, object? payload)
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
                conn.Insert(BuildSyncQueueEntry(EntityTypes.Project, project.SyncId, operation, project));
            });
            return rowsAffected;
        }

        public async Task<int> DeleteProjectAsync(Project project)
        {
            StampForDelete(project);

            var rowsAffected = 0;
            await _db.RunInTransactionAsync(conn =>
            {
                rowsAffected = conn.Delete(project);
                conn.Insert(BuildSyncQueueEntry(EntityTypes.Project, project.SyncId, SyncOperations.Delete, project));
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

        // WL-91: keeps the cross-device parent link in step with the local one on every
        // write, so the queued payload carries a SyncId another device can resolve. Only
        // ever writes a value it actually found - a failed lookup leaves whatever the
        // caller supplied, which is what lets the orchestrator merge a child that arrives
        // before its parent without losing the parent SyncId it was told about.
        private async Task ResolveProjectSyncIdAsync(TaskItem task)
        {
            if (task.ProjectId == 0)
                return;

            var parent = await _db.Table<Project>().Where(p => p.Id == task.ProjectId).FirstOrDefaultAsync();
            if (parent != null)
                task.ProjectSyncId = parent.SyncId;
        }

        private async Task ResolveTaskSyncIdAsync(TimeEntry entry)
        {
            if (entry.TaskItemId == 0)
                return;

            var parent = await _db.Table<TaskItem>().Where(t => t.Id == entry.TaskItemId).FirstOrDefaultAsync();
            if (parent != null)
                entry.TaskSyncId = parent.SyncId;
        }

        public async Task<int> SaveTaskAsync(TaskItem task)
        {
            await ResolveProjectSyncIdAsync(task);

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
                conn.Insert(BuildSyncQueueEntry(EntityTypes.Task, task.SyncId, operation, task));
            });
            return rowsAffected;
        }

        public async Task<int> DeleteTaskAsync(TaskItem task)
        {
            StampForDelete(task);

            var rowsAffected = 0;
            await _db.RunInTransactionAsync(conn =>
            {
                rowsAffected = conn.Delete(task);
                conn.Insert(BuildSyncQueueEntry(EntityTypes.Task, task.SyncId, SyncOperations.Delete, task));
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
            await ResolveTaskSyncIdAsync(entry);

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
                conn.Insert(BuildSyncQueueEntry(EntityTypes.TimeEntry, entry.SyncId, operation, entry));
            });
            return rowsAffected;
        }

        public async Task<int> DeleteTimeEntryAsync(TimeEntry entry)
        {
            StampForDelete(entry);

            var rowsAffected = 0;
            await _db.RunInTransactionAsync(conn =>
            {
                rowsAffected = conn.Delete(entry);
                conn.Insert(BuildSyncQueueEntry(EntityTypes.TimeEntry, entry.SyncId, SyncOperations.Delete, entry));
            });
            return rowsAffected;
        }


        // Expose raw connection if needed
        public SQLiteAsyncConnection Connection => _db;

    }
}
