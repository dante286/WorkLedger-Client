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
        private DateTime _currentStartTime;

        public event Action<TimeSpan>? TimerTick;

        public TimerService(LocalDbService db, AppStateService state)
        {
            _db = db;
            _state = state;

            _timer = new Timer(1000);
            _timer.Elapsed += OnTimerElapsed;
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

            await _db.InsertAsync(entry);

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

            await _db.UpdateAsync(entry);

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
    }
}
