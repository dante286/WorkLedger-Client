using SQLite;
using System.Collections.Generic;
using System.Threading.Tasks;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Services
{
    public class LocalDbService
    {
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

        public Task<int> SaveProjectAsync(Project project)
        {
            if (project.Id == 0)
            {
                StampForInsert(project);
                return _db.InsertAsync(project);
            }

            StampForUpdate(project);
            return _db.UpdateAsync(project);
        }

        public Task<int> DeleteProjectAsync(Project project)
        {
            return _db.DeleteAsync(project);
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

        public Task<int> SaveTaskAsync(TaskItem task)
        {
            if (task.Id == 0)
            {
                StampForInsert(task);
                return _db.InsertAsync(task);
            }

            StampForUpdate(task);
            return _db.UpdateAsync(task);
        }

        public Task<int> DeleteTaskAsync(TaskItem task)
        {
            return _db.DeleteAsync(task);
        }

        // TimeEntry-specific methods
        public Task<List<TimeEntry>> GetTimeEntriesByTaskIdAsync(int taskId)
        {
            return _db.Table<TimeEntry>()
                      .Where(te => te.TaskItemId == taskId)
                      .OrderByDescending(te => te.StartTime)
                      .ToListAsync();
        }

        public Task<int> SaveTimeEntryAsync(TimeEntry entry)
        {
            if (entry.Id == 0)
            {
                StampForInsert(entry);
                return _db.InsertAsync(entry);
            }

            StampForUpdate(entry);
            return _db.UpdateAsync(entry);
        }

        public Task<int> DeleteTimeEntryAsync(TimeEntry entry)
        {
            return _db.DeleteAsync(entry);
        }


        // Expose raw connection if needed
        public SQLiteAsyncConnection Connection => _db;

    }
}
