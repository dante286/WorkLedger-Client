using System;
using System.Threading.Tasks;
using System.Timers;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Services
{
    public class TimerService
    {
        private readonly LocalDbService _db;
        private readonly AppStateService _state;

        private readonly Timer _timer;
        private readonly Timer _backupTimer;
        private DateTime _currentStartTime;

        public event Action<TimeSpan>? TimerTick;
        public event Func<Task>? BackupTick;

        public TimerService(LocalDbService db, AppStateService state)
        {
            _db = db;
            _state = state;

            _timer = new Timer(1000);
            _timer.Elapsed += OnTimerElapsed;
            _backupTimer = new Timer();
            _backupTimer.Elapsed += OnBackupElapsed;
        }

        private async void OnBackupElapsed(object? sender, ElapsedEventArgs e)
        {
            try
            {
                var handler = BackupTick;
                if (handler != null)
                {
                    // Invoke handlers asynchronously, but do not await to avoid blocking timer thread
                    await handler.Invoke();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Backup tick failed: {ex.Message}");
            }
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (_state.ActiveTimeEntry == null)
                return;

            var elapsed = DateTime.UtcNow - _currentStartTime;
            TimerTick?.Invoke(elapsed);
        }

        public async Task StartTimerAsync(TaskItem task)
        {
            // Do not start a timer for a completed task
            if (task.IsCompleted)
                return;

            // Stop any existing timer
            if (_state.ActiveTimeEntry != null)
                await StopTimerAsync();

            var entry = new TimeEntry
            {
                TaskItemId = task.Id,
                StartTime = DateTime.UtcNow
            };

            // WL-97: SaveTimeEntryAsync, not the generic InsertAsync. The generic helpers are
            // bare SQLiteAsyncConnection pass-throughs - they skip the version/SyncId stamping,
            // the parent-SyncId resolution, and above all the SyncQueue row. Timed work is the
            // app's main source of time entries, so writing it raw meant the entries that
            // synced were the hand-added ones and the ones the app exists to record were not.
            await _db.SaveTimeEntryAsync(entry);

            _state.ActiveTask = task;
            _state.ActiveTimeEntry = entry;

            _currentStartTime = entry.StartTime;
            _timer.Start();
        }

        public async Task StopTimerAsync()
        {
            if (_state.ActiveTimeEntry == null)
                return;

            _timer.Stop();

            var entry = _state.ActiveTimeEntry;
            entry.EndTime = DateTime.UtcNow;
            entry.DurationSeconds = (long)(entry.EndTime.Value - entry.StartTime).TotalSeconds;
            entry.UpdatedAt = DateTime.UtcNow;

            // WL-97: same reasoning as StartTimerAsync. The entry already has an Id here, so
            // SaveTimeEntryAsync updates rather than inserts, and the version bump it applies
            // is what lets the completed entry out-version the running one on other devices -
            // without it the stop is invisible and the entry stays open everywhere else.
            await _db.SaveTimeEntryAsync(entry);

            // Clear state
            _state.ActiveTask = null;
            _state.ActiveTimeEntry = null;
        }

        public async Task SwitchTaskAsync(TaskItem newTask)
        {
            await StopTimerAsync();
            await StartTimerAsync(newTask);
        }

        public bool IsRunning => _timer.Enabled;

        /// <summary>
        /// Configure backup interval. If interval is Disabled or OnClose, the backup timer is stopped.
        /// </summary>
        public void ConfigureBackupInterval(WorkLedger_Client.Services.BackupInterval interval)
        {
            try
            {
                if (interval == WorkLedger_Client.Services.BackupInterval.Disabled || interval == WorkLedger_Client.Services.BackupInterval.OnClose)
                {
                    _backupTimer.Stop();
                    return;
                }

                // Determine period in milliseconds (enum values store minutes or special code)
                double minutes = (double)interval;
                var period = TimeSpan.FromMinutes(minutes).TotalMilliseconds;
                if (period <= 0) period = TimeSpan.FromMinutes(1).TotalMilliseconds;

                _backupTimer.Interval = period;
                _backupTimer.AutoReset = true;
                _backupTimer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ConfigureBackupInterval failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs a backup tick and waits up to the provided timeout for it to complete.
        /// </summary>
        public async Task RunOnCloseBackupAsync(TimeSpan timeout)
        {
            var handler = BackupTick;
            if (handler == null) return;

            var task = handler.Invoke();
            if (task == null) return;

            var completed = await Task.WhenAny(task, Task.Delay(timeout));
            if (completed != task)
            {
                System.Diagnostics.Debug.WriteLine("On-close backup timed out");
            }
        }
    }
}
