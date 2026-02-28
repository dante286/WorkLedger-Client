using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using WorkLedger_Client.Views;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WorkLedger_Client.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly TimerService _timerService;
        private readonly AppStateService _appState;

        public ObservableCollection<Project> Projects { get; } = new();
        public ObservableCollection<TaskItemViewModel> Tasks { get; } = new();

        private Project _selectedProject;
        private TaskItemViewModel _selectedTask;
        private TimeEntry _selectedTimeEntry;

        public Project SelectedProject
        {
            get => _selectedProject;
            set
            {
                _selectedProject = value;
                OnPropertyChanged();
                _ = LoadTasksAsync();
            }
        }

        public TaskItemViewModel SelectedTask
        {
            get => _selectedTask;
            set
            {
                _selectedTask = value;
                OnPropertyChanged();
            }
        }

        public TimeEntry SelectedTimeEntry
        {
            get => _selectedTimeEntry;
            set
            {
                _selectedTimeEntry = value;
                OnPropertyChanged();
            }
        }

        private bool _showArchivedTasks = false;

        public bool ShowArchivedTasks
        {
            get => _showArchivedTasks;
            set
            {
                _showArchivedTasks = value;
                OnPropertyChanged();
                _ = LoadTasksAsync();
            }
        }

        public readonly LocalDbService _localDbService;

        public MainWindowViewModel()
        {
            _localDbService = new LocalDbService("workledger.db");
            _appState = new AppStateService();
            _timerService = new TimerService(_localDbService, _appState);
            _timerService.TimerTick += OnTimerTick;
            LoadProjects();
        }

        public MainWindowViewModel(LocalDbService localDbService, TimerService timerService, AppStateService appState)
        {
            _localDbService = localDbService;
            _timerService = timerService;
            _appState = appState;
            _timerService.TimerTick += OnTimerTick;
            LoadProjects();
        }

        public bool IsTaskActive(int taskId, out TimeSpan elapsed)
        {
            elapsed = TimeSpan.Zero;
            if (_appState.ActiveTask?.Id == taskId && _appState.ActiveTimeEntry != null)
            {
                elapsed = DateTime.UtcNow - _appState.ActiveTimeEntry.StartTime;
                return true;
            }
            return false;
        }

        private void OnTimerTick(TimeSpan elapsed)
        {
            if (_appState.ActiveTask == null) return;

            var activeTaskVm = Tasks.FirstOrDefault(t => t.Id == _appState.ActiveTask.Id);
            if (activeTaskVm != null)
            {
                activeTaskVm.CurrentDuration = elapsed.ToString(@"hh\:mm\:ss");
            }
        }

        private async void LoadProjects()
        {
            var projects = await _localDbService.GetProjectsAsync();
            Projects.Clear();
            foreach (var project in projects)
            {
                Projects.Add(project);
            }
        }

        public async Task LoadTasksAsync()
        {
            if (SelectedProject == null) return;

            var tasks = await _localDbService.GetTasksByProjectIdAsync(SelectedProject.Id);
            Tasks.Clear();
            foreach (var task in tasks)
            {
                // Skip archived tasks if not showing them
                if (!ShowArchivedTasks && task.IsArchived)
                    continue;
                    
                var taskViewModel = new TaskItemViewModel(task, this);
                Tasks.Add(taskViewModel);
            }
        }

        public async Task LoadTimeEntriesAsync(TaskItemViewModel taskViewModel)
        {
            if (taskViewModel == null) return;

            var timeEntries = await _localDbService.GetTimeEntriesByTaskIdAsync(taskViewModel.Id);
            taskViewModel.TimeEntries.Clear();
            foreach (var entry in timeEntries)
            {
                taskViewModel.TimeEntries.Add(entry);
            }
        }

        [RelayCommand]
        private async Task CreateProjectAsync()
        {
            var dialog = new ProjectDialog
            {
                Title = "Create Project"
            };

            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow == null) return;

            var result = await dialog.ShowDialog<Project>(desktop.MainWindow);
            if (result != null)
            {
                await _localDbService.SaveProjectAsync(result);
                LoadProjects();
            }
        }

        [RelayCommand]
        private async Task CreateTaskAsync()
        {
            if (SelectedProject == null) return;

            var dialog = new TaskDialog
            {
                Title = "Create Task"
            };

            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow == null) return;

            var result = await dialog.ShowDialog<TaskItem>(desktop.MainWindow);
            if (result != null)
            {
                result.ProjectId = SelectedProject.Id;
                await _localDbService.SaveTaskAsync(result);
                await LoadTasksAsync();
            }
        }

        [RelayCommand]
        private async Task AddTimeEntryAsync()
        {
            if (SelectedTask == null) return;

            var timeEntry = new TimeEntry
            {
                TaskItemId = SelectedTask.Id,
                StartTime = DateTime.UtcNow,
                EndTime = null
            };

            await _localDbService.SaveTimeEntryAsync(timeEntry);
            await LoadTimeEntriesAsync(SelectedTask);
        }

        [RelayCommand]
        private async Task StartTimerAsync()
        {
            if (SelectedTask == null) return;

            // This would be handled by TimerService, not directly in ViewModel
            // For now, we'll just add a time entry
            var timeEntry = new TimeEntry
            {
                TaskItemId = SelectedTask.Id,
                StartTime = DateTime.UtcNow,
                EndTime = null
            };

            await _localDbService.SaveTimeEntryAsync(timeEntry);
            await LoadTimeEntriesAsync(SelectedTask);
        }

        [RelayCommand]
        private async Task StopTimerAsync()
        {
            if (SelectedTask == null || SelectedTimeEntry == null) return;

            SelectedTimeEntry.EndTime = DateTime.UtcNow;
            SelectedTimeEntry.DurationSeconds = (long)(SelectedTimeEntry.EndTime.Value - SelectedTimeEntry.StartTime).TotalSeconds;
            SelectedTimeEntry.UpdatedAt = DateTime.UtcNow;

            await _localDbService.SaveTimeEntryAsync(SelectedTimeEntry);
            await LoadTimeEntriesAsync(SelectedTask);
        }

        [RelayCommand]
        private async Task DeleteProjectAsync(Project project)
        {
            var dialog = new ConfirmationDialog("delete", "project");
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow == null) return;

            var result = await dialog.ShowDialog<bool>(desktop.MainWindow);
            if (result)
            {
                await _localDbService.DeleteProjectAsync(project);
                LoadProjects();
            }
        }

        [RelayCommand]
        private async Task DeleteTaskAsync(TaskItemViewModel task)
        {
            var dialog = new ConfirmationDialog("delete", "task");
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow == null) return;

            var result = await dialog.ShowDialog<bool>(desktop.MainWindow);
            if (result)
            {
                await _localDbService.DeleteTaskAsync(task.GetModel());
                await LoadTasksAsync();
            }
        }

        [RelayCommand]
        private async Task DeleteTimeEntryAsync(TimeEntry timeEntry)
        {
            await _localDbService.DeleteTimeEntryAsync(timeEntry);
            // We need to find which task this time entry belongs to, to refresh it.
            // Since we don't have easy access to the parent task here, we might need to rely on SelectedTask if it matches,
            // or iterate through tasks to find the one containing this entry.
            // For simplicity, if SelectedTask is the parent, refresh it.
            if (SelectedTask != null && SelectedTask.Id == timeEntry.TaskItemId)
            {
                await LoadTimeEntriesAsync(SelectedTask);
            }
            else
            {
                // Fallback: try to find the task in the list
                var parentTask = Tasks.FirstOrDefault(t => t.Id == timeEntry.TaskItemId);
                if (parentTask != null)
                {
                    await LoadTimeEntriesAsync(parentTask);
                }
            }
        }

        [RelayCommand]
        private async Task EditProjectAsync(Project project)
        {
            var dialog = new ProjectDialog(project);
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow == null) return;

            var result = await dialog.ShowDialog<Project>(desktop.MainWindow);
            if (result != null)
            {
                await _localDbService.SaveProjectAsync(result);
                LoadProjects();
            }
        }

        [RelayCommand]
        private async Task EditTaskAsync(TaskItemViewModel taskViewModel)
        {
            var task = taskViewModel.GetModel();
            var dialog = new TaskDialog(task);
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow == null) return;

            var result = await dialog.ShowDialog<TaskItem>(desktop.MainWindow);
            if (result != null)
            {
                task.Name = result.Name;
                task.Notes = result.Notes;
                await _localDbService.SaveTaskAsync(task);
                await LoadTasksAsync();
            }
        }

        [RelayCommand]
        private async Task ToggleTimerAsync(TaskItemViewModel taskViewModel)
        {
            var task = taskViewModel.GetModel();
            TaskItemViewModel? previousTaskVm = null;

            if (_timerService.IsRunning && _appState.ActiveTask?.Id == task.Id)
            {
                await _timerService.StopTimerAsync();
                taskViewModel.CurrentDuration = null;
            }
            else
            {
                // If there's an active task, we'll need to refresh it after it's stopped
                if (_appState.ActiveTask != null)
                {
                    previousTaskVm = Tasks.FirstOrDefault(t => t.Id == _appState.ActiveTask.Id);
                    if (previousTaskVm != null)
                    {
                        previousTaskVm.CurrentDuration = null;
                    }
                }
                await _timerService.StartTimerAsync(task);
            }

            // Refresh the current task's entries
            await taskViewModel.LoadTimeEntriesAsync();

            // Also refresh the previous task's entries if it was stopped
            if (previousTaskVm != null && previousTaskVm != taskViewModel)
            {
                await previousTaskVm.LoadTimeEntriesAsync();
            }
        }

        [RelayCommand]
        private async Task ArchiveTaskAsync(TaskItemViewModel taskViewModel)
        {
            var task = taskViewModel.GetModel();
            task.IsArchived = true;
            await _localDbService.SaveTaskAsync(task);
            await LoadTasksAsync();
        }

        [RelayCommand]
        private async Task UnarchiveTaskAsync(TaskItemViewModel taskViewModel)
        {
            var task = taskViewModel.GetModel();
            task.IsArchived = false;
            await _localDbService.SaveTaskAsync(task);
            await LoadTasksAsync();
        }
    }
}
