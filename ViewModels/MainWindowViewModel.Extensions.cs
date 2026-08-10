using System;
using WorkLedger_Client.Services;

namespace WorkLedger_Client.ViewModels
{
    public partial class MainWindowViewModel
    {
        // Additional constructor overload injected by App to provide ToastService without editing large file
        public MainWindowViewModel(LocalDbService localDbService, TimerService timerService, AppStateService appState, ThemeService themeService, SettingsService settingsService, ToastService toastService)
        {
            _localDbService = localDbService;
            _timerService = timerService;
            _appState = appState;
            _timerService.TimerTick += OnTimerTick;

            _themeService = themeService;
            _settingsService = settingsService;
            _toastService = toastService;

            // Load persisted settings
            var settings = _settingsService.LoadSettings();
            SelectedTheme = string.IsNullOrWhiteSpace(settings.Theme) ? "System" : settings.Theme;
            DataDirectory = settings.DataDirectory ?? string.Empty;
            BackupDirectory = settings.BackupDirectory ?? string.Empty;
            BackupInterval = settings.BackupInterval;
            BackupOnClose = settings.BackupOnClose;
            SyncFolderPath = settings.SyncFolderPath ?? string.Empty;
            ApplicationVersion = string.IsNullOrWhiteSpace(settings.ApplicationVersion)
                ? WorkLedger_Client.Services.AppSettings.CurrentApplicationVersion
                : settings.ApplicationVersion;

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
        }
    }
}
