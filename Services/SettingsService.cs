using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace WorkLedger_Client.Services
{
    public class AppSettings
    {
        public string Theme { get; set; } = "System"; // "System", "Light", "Dark"
        public string DataDirectory { get; set; } = string.Empty;
    }

    public class SettingsService
    {
        private readonly string _dataDirectory;
        private readonly string _settingsPath;

        public const string DefaultDbFileName = "workledger.db";
        public const string SettingsFileName = "settings.json";

        public SettingsService(string? customDataDirectory = null)
        {
            // Determine the data directory
            _dataDirectory = customDataDirectory ?? GetDefaultDataDirectory();

            // Ensure the data directory exists
            if (!Directory.Exists(_dataDirectory))
            {
                Directory.CreateDirectory(_dataDirectory);
            }

            _settingsPath = Path.Combine(_dataDirectory, SettingsFileName);
        }

        /// <summary>
        /// Gets the default data directory based on platform conventions.
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
                // Prefer XDG_CONFIG_HOME, otherwise ~/.config
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (!string.IsNullOrWhiteSpace(xdg))
                    appData = xdg;
                else
                    appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config");
            }
            else
            {
                // Fallback to LocalApplicationData or Personal
                appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(appData))
                    appData = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            }

            return Path.Combine(appData, "WorkLedger");
        }

        // Expose data directory and paths
        public string DataDirectory => _dataDirectory;

        public string SettingsPath => _settingsPath;

        public string DatabasePath => Path.Combine(_dataDirectory, DefaultDbFileName);

        public AppSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    var defaults = new AppSettings
                    {
                        DataDirectory = _dataDirectory
                    };
                    SaveSettings(defaults);
                    return defaults;
                }

                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

                // Backfill missing data directory for older settings files
                if (string.IsNullOrWhiteSpace(settings.DataDirectory))
                {
                    settings.DataDirectory = _dataDirectory;
                    SaveSettings(settings);
                }

                return settings;
            }
            catch (Exception)
            {
                return new AppSettings
                {
                    DataDirectory = _dataDirectory
                };
            }
        }

        public void SaveSettings(AppSettings settings)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates and creates the data directory.
        /// Returns the validated path if successful, or a fallback default path if validation fails.
        /// </summary>
        public string ValidateDataDirectory(string dataDirectoryPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dataDirectoryPath))
                {
                    return _dataDirectory;
                }

                // Ensure the directory exists
                if (!Directory.Exists(dataDirectoryPath))
                {
                    Directory.CreateDirectory(dataDirectoryPath);
                    System.Diagnostics.Debug.WriteLine($"Created data directory: {dataDirectoryPath}");
                }

                return dataDirectoryPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to validate data directory '{dataDirectoryPath}': {ex.Message}");
                return _dataDirectory;
            }
        }

        /// <summary>
        /// Migrates app data (settings and database) from the old default location to a new data directory.
        /// </summary>
        public bool MigrateDataIfNeeded(string newDataDirectory, string? oldDataDirectory = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newDataDirectory))
                    return false;

                var oldDir = oldDataDirectory ?? GetDefaultDataDirectory();

                // If directories are the same, no migration needed
                if (oldDir.Equals(newDataDirectory, StringComparison.OrdinalIgnoreCase))
                    return false;

                // If the old directory doesn't exist, no migration needed
                if (!Directory.Exists(oldDir))
                {
                    System.Diagnostics.Debug.WriteLine($"Old data directory not found at '{oldDir}', no migration needed");
                    return false;
                }

                // Ensure the new directory exists
                if (!Directory.Exists(newDataDirectory))
                {
                    Directory.CreateDirectory(newDataDirectory);
                    System.Diagnostics.Debug.WriteLine($"Created new data directory: {newDataDirectory}");
                }

                bool migratedAny = false;

                // Migrate settings.json
                var oldSettingsPath = Path.Combine(oldDir, SettingsFileName);
                var newSettingsPath = Path.Combine(newDataDirectory, SettingsFileName);
                if (File.Exists(oldSettingsPath))
                {
                    File.Copy(oldSettingsPath, newSettingsPath, overwrite: true);
                    System.Diagnostics.Debug.WriteLine($"Migrated settings from '{oldSettingsPath}' to '{newSettingsPath}'");
                    migratedAny = true;
                }

                // Migrate database
                var oldDbPath = Path.Combine(oldDir, DefaultDbFileName);
                var newDbPath = Path.Combine(newDataDirectory, DefaultDbFileName);
                if (File.Exists(oldDbPath))
                {
                    // Create backup if database already exists at destination
                    if (File.Exists(newDbPath))
                    {
                        var backupPath = newDbPath + ".backup";
                        File.Copy(newDbPath, backupPath, overwrite: true);
                        System.Diagnostics.Debug.WriteLine($"Created backup of existing database: {backupPath}");
                    }

                    File.Copy(oldDbPath, newDbPath, overwrite: true);
                    System.Diagnostics.Debug.WriteLine($"Migrated database from '{oldDbPath}' to '{newDbPath}'");
                    migratedAny = true;
                }

                if (migratedAny)
                {
                    System.Diagnostics.Debug.WriteLine($"Data migration completed from '{oldDir}' to '{newDataDirectory}'");
                }

                return migratedAny;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to migrate data: {ex.Message}");
                return false;
            }
        }
    }
}
