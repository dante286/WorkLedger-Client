using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.ViewModels
{
    public partial class TaskItemViewModel : ViewModelBase
    {
        private readonly TaskItem _taskItem;
        private readonly MainWindowViewModel _parentViewModel;

        public TaskItemViewModel(TaskItem taskItem, MainWindowViewModel parentViewModel)
        {
            _taskItem = taskItem;
            _parentViewModel = parentViewModel;
            _name = taskItem.Name ?? string.Empty;
            _notes = taskItem.Notes ?? string.Empty;
            _isCompleted = taskItem.IsCompleted;
            _isArchived = taskItem.IsArchived;
            _ = LoadTimeEntriesAsync();
        }

        public int Id => _taskItem.Id;
        public int ProjectId => _taskItem.ProjectId;

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private string _notes;

        [ObservableProperty]
        private bool _isCompleted;

        [ObservableProperty]
        private bool _isArchived;

        [ObservableProperty]
        private string? _currentDuration;

        [ObservableProperty]
        private string _totalTime = "00:00:00";

        [ObservableProperty]
        private bool _isExpanded;

        public ObservableCollection<TimeEntry> TimeEntries { get; } = [];

        public async Task LoadTimeEntriesAsync()
        {
            var entries = await _parentViewModel._localDbService.GetTimeEntriesByTaskIdAsync(Id);
            TimeEntries.Clear();
            long totalSeconds = 0;
            foreach (var entry in entries)
            {
                if (entry.EndTime != null)
                {
                    TimeEntries.Add(entry);
                    totalSeconds += entry.DurationSeconds ?? 0;
                }
            }
            TotalTime = TimeSpan.FromSeconds(totalSeconds).ToString(@"hh\:mm\:ss");
            
            // If this task is active, initialize CurrentDuration
            if (_parentViewModel.IsTaskActive(Id, out var elapsed))
            {
                CurrentDuration = elapsed.ToString(@"hh\:mm\:ss");
            }
            else
            {
                CurrentDuration = null;
            }
        }

        async partial void OnIsCompletedChanged(bool value)
        {
            _taskItem.IsCompleted = value;
            await _parentViewModel._localDbService.SaveTaskAsync(_taskItem);
            await _parentViewModel.LoadTasksAsync();
        }

        public TaskItem GetModel()
        {
            _taskItem.Name = Name;
            _taskItem.Notes = Notes;
            _taskItem.IsCompleted = IsCompleted;
            _taskItem.IsArchived = IsArchived;
            return _taskItem;
        }
    }
}
