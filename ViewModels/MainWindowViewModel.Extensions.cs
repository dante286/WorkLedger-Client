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
            ApplicationVersion = string.IsNullOrWhiteSpace(settings.ApplicationVersion)
                ? WorkLedger_Client.Services.AppSettings.CurrentApplicationVersion
                : settings.ApplicationVersion;

            LoadProjects();
        }
    }
}
