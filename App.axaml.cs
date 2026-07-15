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

                // PHASE 1: DISCOVER the correct data directory
                // This checks if user has previously configured a custom directory and returns it
                string dataDirectory = DiscoverDataDirectory();
                System.Diagnostics.Debug.WriteLine($"Discovered data directory: {dataDirectory}");

                // PHASE 2: INITIALIZE SettingsService with the discovered directory
                // This SINGLE initialization ensures all settings operations use the correct location
                var settingsService = new SettingsService(dataDirectory);
                var appSettings = settingsService.LoadSettings();
                System.Diagnostics.Debug.WriteLine($"Loaded settings from: {settingsService.SettingsPath}");

                // PHASE 3: HANDLE LEGACY MIGRATIONS
                // Attempt to migrate data from old default location if needed
                var defaultDir = GetDefaultDataDirectory();
                if (!dataDirectory.Equals(defaultDir, StringComparison.OrdinalIgnoreCase))
                {
                    // User has custom directory configured - check if there's legacy data in old default location
                    var oldDefaultDir = defaultDir;
                    if (Directory.Exists(oldDefaultDir) && !Directory.Equals(new DirectoryInfo(oldDefaultDir), new DirectoryInfo(dataDirectory)))
                    {
                        if (settingsService.MigrateDataIfNeeded(dataDirectory, oldDefaultDir))
                        {
                            System.Diagnostics.Debug.WriteLine("Legacy data migration from old default completed");
                        }
                    }
                }

                // Database path is now in the discovered data directory
                string dbPath = settingsService.DatabasePath;
                System.Diagnostics.Debug.WriteLine($"Database path: {dbPath}");

                // Initialize services with settled paths
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

        /// <summary>
        /// Discovers the correct data directory by checking if a user-configured one exists in settings.
        /// Returns the user-configured directory if found and different from default, otherwise returns default.
        /// This DOES NOT load settings from the discovered location—just determines WHERE settings should be.
        /// </summary>
        private string DiscoverDataDirectory()
        {
            try
            {
                // Get default data directory
                var defaultDir = GetDefaultDataDirectory();
                string settingsPath = Path.Combine(defaultDir, "settings.json");

                // If settings.json doesn't exist in default location, use default
                if (!File.Exists(settingsPath))
                {
                    System.Diagnostics.Debug.WriteLine($"Settings not found at {settingsPath}, using default directory");
                    return defaultDir;
                }

                // Try to read DataDirectory from existing settings.json
                try
                {
                    var json = File.ReadAllText(settingsPath);
                    var settings = System.Text.Json.JsonSerializer.Deserialize<Services.AppSettings>(json);

                    if (settings != null && 
                        !string.IsNullOrWhiteSpace(settings.DataDirectory) && 
                        !settings.DataDirectory.Equals(defaultDir, StringComparison.OrdinalIgnoreCase))
                    {
                        System.Diagnostics.Debug.WriteLine($"Found user-configured data directory: {settings.DataDirectory}");
                        return settings.DataDirectory;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to read DataDirectory from settings: {ex.Message}");
                }

                // Fall back to default
                return defaultDir;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error discovering data directory: {ex.Message}");
                return GetDefaultDataDirectory();
            }
        }

        /// <summary>
        /// Gets the default data directory based on platform conventions.
        /// Mirrors the logic in SettingsService.GetDefaultDataDirectory()
        /// </summary>
        private static string GetDefaultDataDirectory()
        {
            string appData;

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                appData = Path.Combine(home, "Library", "Application Support");
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (!string.IsNullOrWhiteSpace(xdg))
                    appData = xdg;
                else
                    appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config");
            }
            else
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(appData))
                    appData = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            }

            return Path.Combine(appData, "WorkLedger");
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
