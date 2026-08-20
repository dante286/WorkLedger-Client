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

        private readonly SettingsService _settingsService;
        private readonly ThemeService _themeService;
        private readonly ToastService? _toastService;
        private readonly SyncCoordinator _syncCoordinator;
        private readonly ReportsService _reportsService;

        // WL-67/WL-95: the single live AppSettings instance, shared with SyncCoordinator and
        // through it with SettingsSyncStore. Every save mutates and re-persists *this* object
        // rather than building a fresh AppSettings from the bound properties: the settings
        // screen doesn't know about DeviceId, the issued API token, or the pull cursor, so a
        // freshly-constructed one would write those back as empty - re-minting the DeviceId on
        // the next load and making this install look like a brand new device to every peer.
        private readonly AppSettings _settings;

        private CancellationTokenSource? _autosaveCts;

        public System.Collections.ObjectModel.ObservableCollection<string> ThemeOptions { get; } = new System.Collections.ObjectModel.ObservableCollection<string> { "System", "Light", "Dark" };

        public System.Collections.ObjectModel.ObservableCollection<BackupInterval> BackupIntervalOptions { get; } = new System.Collections.ObjectModel.ObservableCollection<BackupInterval>
        {
            BackupInterval.Disabled,
            BackupInterval.OneMinute,
            BackupInterval.FiveMinutes,
            BackupInterval.OneHour
        };

        [ObservableProperty]
        private string _selectedTheme = "System";

        [ObservableProperty]
        private string _dataDirectory = string.Empty;

        [ObservableProperty]
        private string _backupDirectory = string.Empty;

        [ObservableProperty]
        private BackupInterval _backupInterval = BackupInterval.Disabled;

        [ObservableProperty]
        private bool _backupOnClose;

        [ObservableProperty]
        private string _syncFolderPath = string.Empty;

        [ObservableProperty]
        private string _applicationVersion = WorkLedger_Client.Services.AppSettings.CurrentApplicationVersion;

        // WL-67: the one constructor. There used to be three - a parameterless one that
        // hardcoded its own LocalDbService, a five-arg one without ToastService, and this
        // one - of which only this was ever called. Plan 7 adds another dependency
        // (SyncCoordinator), which is the point at which keeping three of these in step
        // stopped being worth it.
        public MainWindowViewModel(
            LocalDbService localDbService,
            TimerService timerService,
            AppStateService appState,
            ThemeService themeService,
            SettingsService settingsService,
            ToastService toastService,
            AppSettings settings,
            SyncCoordinator syncCoordinator)
        {
            _localDbService = localDbService;
            _timerService = timerService;
            _appState = appState;
            _timerService.TimerTick += OnTimerTick;

            _themeService = themeService;
            _settingsService = settingsService;
            _toastService = toastService;
            _settings = settings;
            _syncCoordinator = syncCoordinator;
            _reportsService = new ReportsService(localDbService);

            SelectedTheme = string.IsNullOrWhiteSpace(settings.Theme) ? "System" : settings.Theme;
            DataDirectory = settings.DataDirectory ?? string.Empty;
            BackupDirectory = settings.BackupDirectory ?? string.Empty;
            BackupInterval = settings.BackupInterval;
            BackupOnClose = settings.BackupOnClose;
            SyncFolderPath = settings.SyncFolderPath ?? string.Empty;
            SyncMode = settings.SyncMode;
            SyncInterval = settings.SyncInterval;
            PreMergeBackupMode = settings.PreMergeBackupMode;
            ApiEndpoint = settings.ApiEndpoint ?? string.Empty;
            DeviceId = settings.DeviceId ?? string.Empty;
            ApplicationVersion = string.IsNullOrWhiteSpace(settings.ApplicationVersion)
                ? WorkLedger_Client.Services.AppSettings.CurrentApplicationVersion
                : settings.ApplicationVersion;

            RefreshSyncStatus();

            // A timer-driven sync updates the cursor without anyone pressing anything, so the
            // displayed "last synced" has to follow the orchestrator rather than the button.
            _syncCoordinator.SyncCompleted += OnSyncCompleted;

            try
            {
                _timerService.ConfigureBackupInterval(settings.BackupInterval);
            }
            catch { }

            _timerService.BackupTick += async () =>
            {
                try
                {
                    // No interval gate here: this handler also fires for the on-close backup,
                    // which can be enabled independently while BackupInterval is Disabled.
                    // The periodic timer itself is only running when BackupInterval != Disabled,
                    // and the on-close call site only fires when BackupOnClose is set.
                    if (string.IsNullOrWhiteSpace(BackupDirectory)) return;
                    var ok = await _settingsService.CreateBackupAsync(BackupDirectory);
                    _toastService?.ShowToast(ok ? "Backup saved" : "Backup failed", 1400);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Backup handler error: {ex.Message}");
                }
            };

            LoadProjects();
            _ = LoadWeeklyReportAsync();
        }

        // Copies the bound settings values onto the live AppSettings instance and persists it.
        // Everything the settings screen doesn't surface (DeviceId, ApiToken, LastSyncedAtUtc,
        // SyncRetentionDays) survives untouched because this mutates rather than rebuilds.
        private void PersistSettings()
        {
            _settings.Theme = SelectedTheme;
            _settings.DataDirectory = DataDirectory;
            _settings.BackupDirectory = BackupDirectory;
            _settings.BackupInterval = BackupInterval;
            _settings.BackupOnClose = BackupOnClose;
            _settings.SyncFolderPath = SyncFolderPath;
            _settings.SyncMode = SyncMode;
            _settings.SyncInterval = SyncInterval;
            _settings.PreMergeBackupMode = PreMergeBackupMode;
            _settings.ApiEndpoint = ApiEndpoint;
            _settings.ApplicationVersion = ApplicationVersion;

            _settingsService.SaveSettings(_settings);

            // Picks up a changed mode/folder/endpoint/interval, and is a no-op when none of
            // them moved.
            _syncCoordinator.ApplyConfiguration();
        }

        private void ApplySelectedTheme()
        {
            if (SelectedTheme.Equals("System", StringComparison.OrdinalIgnoreCase))
            {
                var isDark = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
                _themeService.SetTheme(isDark == true ? "Dark" : "Light");
            }
            else
            {
                _themeService.SetTheme(SelectedTheme);
            }
        }

        [RelayCommand]
        private void SaveSettings()
        {
            try
            {
                PersistSettings();
                ApplySelectedTheme();
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

        partial void OnBackupDirectoryChanged(string value)
        {
            TriggerAutosave();
        }

        partial void OnBackupIntervalChanged(BackupInterval value)
        {
            _timerService.ConfigureBackupInterval(value);
            TriggerAutosave();
        }

        partial void OnBackupOnCloseChanged(bool value)
        {
            TriggerAutosave();
        }

        partial void OnSyncFolderPathChanged(string value)
        {
            TriggerAutosave();
        }

        partial void OnSyncModeChanged(SyncMode value)
        {
            OnPropertyChanged(nameof(IsFileSyncSelected));
            OnPropertyChanged(nameof(IsApiSyncSelected));
            OnPropertyChanged(nameof(IsSyncConfigured));
            TriggerAutosave();
        }

        partial void OnSyncIntervalChanged(SyncInterval value)
        {
            TriggerAutosave();
        }

        partial void OnPreMergeBackupModeChanged(PreMergeBackupMode value)
        {
            TriggerAutosave();
        }

        partial void OnApiEndpointChanged(string value)
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

                    PersistSettings();
                    ApplySelectedTheme();

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
            
            // Calculate weekly total.
            //
            // WL-102: "this week" is the ISO week starting Monday, computed by the same helper
            // the Reports tab uses - which is also the rule the server's /reports/weekly
            // applies. This used to subtract (int)DayOfWeek, which numbers Sunday as 0 and so
            // started a new week every Sunday, one day ahead of the server.
            long weeklySeconds = 0;
            var weekStart = ReportsService.StartOfIsoWeek(DateTime.UtcNow);
            var weekEnd = weekStart.AddDays(7);

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
                    if (entry.EndTime != null && entry.StartTime >= weekStart && entry.StartTime < weekEnd)
                    {
                        weeklySeconds += entry.DurationSeconds ?? 0;
                    }
                }
            }
            // Shared with the Reports tab so a week over 24 hours reads as e.g. 41:12:00
            // rather than silently wrapping the way TimeSpan's "hh" specifier does.
            WeeklyTotal = ReportDuration.Format(weeklySeconds);
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
                    _toastService?.ShowToast("Selected folder has no accessible local path", 3000);
                    return;
                }

                // Validate write permissions to the selected directory using normal filesystem APIs when a local path is available
                try
                {
                    var testFile = Path.Combine(path, ".workledger_test");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);

                    var currentDataDirectory = _settingsService.DataDirectory;

                    if (path.Equals(currentDataDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        _toastService?.ShowToast("This is already the current data directory", 2000);
                        return;
                    }

                    // Copy the live settings + database into the newly selected directory right now,
                    // instead of deferring to App.axaml.cs's next-launch heuristic, which only ever
                    // handled the default -> custom direction and left custom -> default (and
                    // custom -> custom) moves with nothing to copy the real data back (WL-74).
                    // Any pre-existing file at the destination (e.g. a stale copy left behind by an
                    // earlier move) is backed up rather than silently discarded.
                    _settingsService.MigrateDataIfNeeded(path, currentDataDirectory);

                    DataDirectory = path;

                    // Mutates the live AppSettings - keeping DeviceId, the issued API token
                    // and the pull cursor, none of which the settings screen holds - and
                    // rewrites it at the current location before anything points elsewhere.
                    PersistSettings();

                    // Persist the settings at their new home...
                    new SettingsService(path).SaveSettings(_settings);

                    // ...and refresh the pointer settings.json at the OS default location, since
                    // DiscoverDataDirectory() always bootstraps by reading settings.json there to
                    // find wherever the real data currently lives.
                    var defaultDir = SettingsService.GetDefaultDataDirectory();
                    if (!defaultDir.Equals(path, StringComparison.OrdinalIgnoreCase))
                    {
                        new SettingsService(defaultDir).SaveSettings(_settings);
                    }

                    _toastService?.ShowToast("Data directory changed. Restart the app to finish switching over.", 3000);
                }
                catch (UnauthorizedAccessException)
                {
                    _toastService?.ShowToast("Access denied: Cannot write to the selected location", 3000);
                    System.Diagnostics.Debug.WriteLine("Access denied when selecting data directory");
                }
                catch (Exception ex)
                {
                    _toastService?.ShowToast($"Cannot write to this location: {ex.Message}", 3000);
                    System.Diagnostics.Debug.WriteLine($"Error validating data directory: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _toastService?.ShowToast("Failed to browse for data directory", 3000);
                System.Diagnostics.Debug.WriteLine($"Error browsing for data directory: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task PickBackupFolderAsync()
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

                var path = picked[0]?.Path?.LocalPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    _toastService?.ShowToast("Selected folder has no accessible local path", 3000);
                    return;
                }

                BackupDirectory = path;
                _toastService?.ShowToast("Backup folder selected", 1400);
            }
            catch (Exception ex)
            {
                _toastService?.ShowToast("Failed to pick backup folder", 3000);
                System.Diagnostics.Debug.WriteLine($"Error picking backup folder: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task PickSyncFolderAsync()
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

                var path = picked[0]?.Path?.LocalPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    _toastService?.ShowToast("Selected folder has no accessible local path", 3000);
                    return;
                }

                if (_settingsService != null && !_settingsService.ValidateSyncFolder(path))
                {
                    _toastService?.ShowToast("Selected folder is not usable for sync (not writable)", 3000);
                    return;
                }

                SyncFolderPath = path;
                _toastService?.ShowToast("Sync folder selected", 1400);
            }
            catch (Exception ex)
            {
                _toastService?.ShowToast("Failed to pick sync folder", 3000);
                System.Diagnostics.Debug.WriteLine($"Error picking sync folder: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task RestoreFromFolderAsync()
        {
            if (_settingsService == null)
                return;

            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow == null) return;

            try
            {
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel?.StorageProvider == null)
                    return;

                var picked = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
                if (picked == null || picked.Count == 0) return;

                var path = picked[0]?.Path?.LocalPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    _toastService?.ShowToast("Selected folder has no accessible local path", 3000);
                    return;
                }

                var (success, error) = await _settingsService.RestoreFromDirectoryAsync(path);
                if (success)
                {
                    _toastService?.ShowToast("Restore completed. Restart the app to load restored data.", 3500);
                    return;
                }

                _toastService?.ShowToast($"Restore failed: {error}", 3500);
            }
            catch (Exception ex)
            {
                _toastService?.ShowToast("Restore failed", 3000);
                System.Diagnostics.Debug.WriteLine($"Error restoring from folder: {ex.Message}");
            }
        }
    }
}
