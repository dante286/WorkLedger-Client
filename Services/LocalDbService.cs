using SQLite;
using System.Collections.Generic;
using System.Threading.Tasks;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Services
{
    public class LocalDbService
    {
        private readonly SQLiteAsyncConnection _db;

        public LocalDbService(string dbPath)
        {
            _db = new SQLiteAsyncConnection(dbPath);

            // Fire-and-forget initialization, explicitly discarding the task
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            await _db.CreateTableAsync<Project>();
            await _db.CreateTableAsync<TaskItem>();
            await _db.CreateTableAsync<TimeEntry>();
            await _db.CreateTableAsync<SyncQueue>();
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
                return _db.InsertAsync(project);

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
                return _db.InsertAsync(task);

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
                return _db.InsertAsync(entry);

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
