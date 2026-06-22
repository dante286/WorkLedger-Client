using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using WorkLedger_Client.ViewModels;
using WorkLedger_Client.Views;
using WorkLedger_Client.Services;
using System.IO;
using Avalonia.Threading;

namespace WorkLedger_Client
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
                // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
                DisableAvaloniaDataAnnotationValidation();

                // Initialize settings service with default data directory
                var settingsService = new SettingsService();
                var appSettings = settingsService.LoadSettings();

                // If user has configured a custom data directory, reinitialize the settings service with it
                string dataDirectory = settingsService.DataDirectory;
                if (!string.IsNullOrWhiteSpace(appSettings.DataDirectory) && !appSettings.DataDirectory.Equals(dataDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    dataDirectory = appSettings.DataDirectory;
                    settingsService = new SettingsService(dataDirectory);
                    appSettings = settingsService.LoadSettings();
                }

                // Attempt to migrate data from old default location if needed
                var oldDefaultDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WorkLedger"
                );
                if (Directory.Exists(oldDefaultDir) && !dataDirectory.Equals(oldDefaultDir, StringComparison.OrdinalIgnoreCase))
                {
                    if (settingsService.MigrateDataIfNeeded(dataDirectory, oldDefaultDir))
                    {
                        System.Diagnostics.Debug.WriteLine("Data migration completed successfully");
                    }
                }

                // Database path is now in the data directory
                string dbPath = settingsService.DatabasePath;

                // Initialize services
                var localDb = new LocalDbService(dbPath);
                var appState = new AppStateService();
                var timerService = new TimerService(localDb, appState);

                // Initialize theme service with system detection
                var themeService = new ThemeService(desktop);

                // Apply persisted theme if user selected one
                if (!string.IsNullOrWhiteSpace(appSettings.Theme) && !appSettings.Theme.Equals("System", StringComparison.OrdinalIgnoreCase))
                {
                    themeService.SetTheme(appSettings.Theme);
                }

                // Initialize toast service for transient messages
                var toastService = new ToastService(desktop);

                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(localDb, timerService, appState, themeService, settingsService, toastService),
                };
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void DisableAvaloniaDataAnnotationValidation()
        {
            // Get an array of plugins to remove
            var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            // remove each entry found
            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }
    }
}
