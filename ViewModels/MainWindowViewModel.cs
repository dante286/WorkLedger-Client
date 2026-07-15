using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
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

        private Project? _selectedProject;
        private TaskItemViewModel? _selectedTask;
        private TimeEntry? _selectedTimeEntry;

        public Project? SelectedProject
        {
            get => _selectedProject;
            set
            {
                _selectedProject = value;
                OnPropertyChanged();
                _ = LoadTasksAsync();
            }
        }

        public TaskItemViewModel? SelectedTask
        {
            get => _selectedTask;
            set
            {
                _selectedTask = value;
                OnPropertyChanged();
            }
        }

        public TimeEntry? SelectedTimeEntry
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

        private readonly SettingsService? _settingsService;
        private readonly ThemeService? _themeService;
        private readonly ToastService? _toastService;
        private CancellationTokenSource? _autosaveCts;

        public System.Collections.ObjectModel.ObservableCollection<string> ThemeOptions { get; } = new System.Collections.ObjectModel.ObservableCollection<string> { "System", "Light", "Dark" };

        [ObservableProperty]
        private string _selectedTheme = "System";

        [ObservableProperty]
        private string _dataDirectory = string.Empty;

        [ObservableProperty]
        private string _applicationVersion = WorkLedger_Client.Services.AppSettings.CurrentApplicationVersion;

        
        public MainWindowViewModel()
        {
            _localDbService = new LocalDbService("workledger.db");
            _appState = new AppStateService();
            _timerService = new TimerService(_localDbService, _appState);
            _timerService.TimerTick += OnTimerTick;
            LoadProjects();
        }

        public MainWindowViewModel(LocalDbService localDbService, TimerService timerService, AppStateService appState, ThemeService themeService, SettingsService settingsService)
        {
            _localDbService = localDbService;
            _timerService = timerService;
            _appState = appState;
            _timerService.TimerTick += OnTimerTick;

            _themeService = themeService;
            _settingsService = settingsService;

            // Load persisted settings
            var settings = _settingsService.LoadSettings();
            SelectedTheme = string.IsNullOrWhiteSpace(settings.Theme) ? "System" : settings.Theme;
            DataDirectory = settings.DataDirectory ?? string.Empty;
            ApplicationVersion = string.IsNullOrWhiteSpace(settings.ApplicationVersion)
                ? WorkLedger_Client.Services.AppSettings.CurrentApplicationVersion
                : settings.ApplicationVersion;

            LoadProjects();
        }

        [RelayCommand]
        private async Task SaveSettings()
        {
            try
            {
                var settings = new WorkLedger_Client.Services.AppSettings
                {
                    Theme = SelectedTheme,
                    DataDirectory = DataDirectory,
                    ApplicationVersion = ApplicationVersion
                };
                _settingsService!.SaveSettings(settings);

                if (SelectedTheme.Equals("System", StringComparison.OrdinalIgnoreCase))
                {
                    var isDark = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
                    _themeService!.SetTheme(isDark == true ? "Dark" : "Light");
                }
                else
                {
                    _themeService!.SetTheme(SelectedTheme);
                }

                _toastService?.ShowToast("Settings saved", 1400);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
                _toastService?.ShowToast("Failed to save settings", 1600);
            }
        }

        // Autosave: debounce changes and persist automatically
        partial void OnSelectedThemeChanged(string value)
        {
            TriggerAutosave();
        }

        partial void OnDataDirectoryChanged(string value)
        {
            TriggerAutosave();
        }

        private void TriggerAutosave()
        {
            // Cancel previous scheduled save
            _autosaveCts?.Cancel();
            _autosaveCts = new CancellationTokenSource();
            var token = _autosaveCts.Token;

            // Fire-and-forget debounce task
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(800, token); // debounce interval
                    if (token.IsCancellationRequested) return;

                    // Perform save (no long confirmation)
                    var settings = new WorkLedger_Client.Services.AppSettings
                    {
                        Theme = SelectedTheme,
                        DataDirectory = DataDirectory,
                        ApplicationVersion = ApplicationVersion
                    };

                    _settingsService!.SaveSettings(settings);

                    if (SelectedTheme.Equals("System", StringComparison.OrdinalIgnoreCase))
                    {
                        var isDark = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
                        _themeService!.SetTheme(isDark == true ? "Dark" : "Light");
                    }
                    else
                    {
                        _themeService!.SetTheme(SelectedTheme);
                    }

                    _toastService?.ShowToast("Autosaved", 1000);
                }
                catch (TaskCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Autosave error: {ex.Message}");
                }
            }, token);
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

        [ObservableProperty]
        private int? _activeTaskId;

        [ObservableProperty]
        private int? _activeProjectId;

        [ObservableProperty]
        private string _activeProjectName = string.Empty;

        [ObservableProperty]
        private string _activeTimerValue = "00:00:00";

        [ObservableProperty]
        private string _weeklyTotal = "00:00:00";

        [ObservableProperty]
        private int _taskCount;

        private void OnTimerTick(TimeSpan elapsed)
        {
            ActiveTimerValue = elapsed.ToString(@"hh\:mm\:ss");
            
            if (_appState.ActiveTask == null)
            {
                ActiveTaskId = null;
                ActiveProjectId = null;
                ActiveProjectName = string.Empty;
                return;
            }

            ActiveTaskId = _appState.ActiveTask.Id;
            ActiveProjectId = _appState.ActiveTask.ProjectId;
            ActiveProjectName = Projects.FirstOrDefault(p => p.Id == ActiveProjectId.Value)?.Name ?? string.Empty;

            var activeTaskVm = Tasks.FirstOrDefault(t => t.Id == _appState.ActiveTask.Id);
            if (activeTaskVm != null)
            {
                activeTaskVm.CurrentDuration = ActiveTimerValue;
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
            if (SelectedProject == null)
            {
                Tasks.Clear();
                TaskCount = 0;
                WeeklyTotal = "00:00:00";
                return;
            }

            var tasks = await _localDbService.GetTasksByProjectIdAsync(SelectedProject.Id);
            Tasks.Clear();
            TaskCount = tasks.Count;
            
            // Calculate weekly total
            long weeklySeconds = 0;
            var weekStart = DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek);
            
            foreach (var task in tasks)
            {
                // Skip archived tasks if not showing them
                if (!ShowArchivedTasks && task.IsArchived)
                    continue;
                    
                var taskViewModel = new TaskItemViewModel(task, this);
                Tasks.Add(taskViewModel);
                
                // Fetch time entries for this task to calculate weekly total
                var entries = await _localDbService.GetTimeEntriesByTaskIdAsync(task.Id);
                foreach (var entry in entries)
                {
                    if (entry.EndTime != null && entry.StartTime >= weekStart)
                    {
                        weeklySeconds += entry.DurationSeconds ?? 0;
                    }
                }
            }
            WeeklyTotal = TimeSpan.FromSeconds(weeklySeconds).ToString(@"hh\:mm\:ss");
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

                // Clear active indicators so the UI updates (hides pills/dots)
                ActiveTaskId = null;
                ActiveProjectId = null;
                ActiveProjectName = string.Empty;
                ActiveTimerValue = "00:00:00";
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
        private async Task ToggleArchiveTaskAsync(TaskItemViewModel taskViewModel)
        {
            var task = taskViewModel.GetModel();
            task.IsArchived = !task.IsArchived;
            await _localDbService.SaveTaskAsync(task);
            await LoadTasksAsync();
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

        [RelayCommand]
        private async Task BrowseDataDirectoryAsync()
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow == null) return;

            try
            {
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel?.StorageProvider == null)
                    return;

                var picked = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
                if (picked == null || picked.Count == 0) return;

                string? path = picked[0]?.Path?.LocalPath;

                if (string.IsNullOrWhiteSpace(path))
                {
                    _toastService?.ShowToast("Selected folder has no accessible local path", 2400);
                    return;
                }

                // Validate write permissions to the selected directory using normal filesystem APIs when a local path is available
                try
                {
                    var testFile = Path.Combine(path, ".workledger_test");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);

                    DataDirectory = path;
                    _toastService?.ShowToast("Data directory set. Restart the app to migrate data.", 2000);
                }
                catch (UnauthorizedAccessException)
                {
                    _toastService?.ShowToast("Access denied: Cannot write to the selected location", 2000);
                    System.Diagnostics.Debug.WriteLine("Access denied when selecting data directory");
                }
                catch (Exception ex)
                {
                    _toastService?.ShowToast($"Cannot write to this location: {ex.Message}", 2000);
                    System.Diagnostics.Debug.WriteLine($"Error validating data directory: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _toastService?.ShowToast("Failed to browse for data directory", 2000);
                System.Diagnostics.Debug.WriteLine($"Error browsing for data directory: {ex.Message}");
            }
        }
    }
}
