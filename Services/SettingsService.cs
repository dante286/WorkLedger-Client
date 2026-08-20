using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WorkLedger_Client.Services
{
    public class AppSettings
    {
        public const string CurrentApplicationVersion = "1.0.4";

        public string Theme { get; set; } = "System"; // "System", "Light", "Dark"
        public string DataDirectory { get; set; } = string.Empty;
        public string ApplicationVersion { get; set; } = CurrentApplicationVersion;
        public string BackupDirectory { get; set; } = string.Empty;
        public BackupInterval BackupInterval { get; set; } = BackupInterval.Disabled;
        public bool BackupOnClose { get; set; } = false;
        // Stable per-install identifier, minted at first run. This is also the DeviceId
        // ApiSyncProvider registers with the server and the OriginDeviceId stamped on every
        // row this install writes - there is deliberately only one of these.
        public string DeviceId { get; set; } = string.Empty;
        public string SyncFolderPath { get; set; } = string.Empty;

        // Base URL of the WorkLedger-API instance, e.g. "https://workledger.example.com".
        public string ApiEndpoint { get; set; } = string.Empty;

        // The JWT the *server* issued to this device via POST /auth/register-device - not a
        // hand-entered secret. Persisted so a restart doesn't force a re-bootstrap.
        public string ApiToken { get; set; } = string.Empty;

        // From RegisterDeviceResponse.ExpiresAtUtc, so the provider can re-bootstrap ahead of
        // expiry instead of discovering it as a 401 mid-sync. Nullable: "never bootstrapped"
        // and "expired long ago" are different states and DateTime.MinValue conflates them.
        public DateTime? ApiTokenExpiresAtUtc { get; set; }

        // WL-92: the pull cursor, persisted so it survives a restart, and holding the value
        // the *provider* reported rather than this device's own clock. There is exactly one
        // "last synced" timestamp in this app by design - if a Sync settings section needs
        // something to display (WL-95), it displays this.
        public DateTime? LastSyncedAtUtc { get; set; }

        // WL-61: how long FileSyncProvider keeps processed change files in the sync folder
        // before pruning them. Value == days, mirroring BackupInterval's "value == minutes"
        // convention. Unlike the string settings above, an int naturally survives a missing
        // key in older settings.json (System.Text.Json leaves this initializer's value
        // untouched), so no explicit LoadSettings backfill is needed.
        public int SyncRetentionDays { get; set; } = 30;

        // Which ISyncProvider the app drives, if any. Off is the default and the only value
        // that leaves SyncOrchestrator idle - see SyncCoordinator, which owns the mapping
        // from this to a concrete provider.
        public SyncMode SyncMode { get; set; } = SyncMode.Off;

        // How often the background sync tick fires. Value == minutes, the same convention
        // BackupInterval uses. Only consulted when SyncMode != Off; "Sync now" is always
        // available regardless.
        public SyncInterval SyncInterval { get; set; } = SyncInterval.FifteenMinutes;

        // WL-51: whether SyncOrchestrator takes a safety backup before the first remote-wins
        // overwrite/delete of a sync run. Defaults to OncePerSession: enough to make a bad
        // merge recoverable without copying the database on every single run.
        public PreMergeBackupMode PreMergeBackupMode { get; set; } = PreMergeBackupMode.OncePerSession;
    }

    public enum SyncMode
    {
        Off = 0,

        // FileSyncProvider over AppSettings.SyncFolderPath - a shared folder, no backend.
        File = 1,

        // ApiSyncProvider against AppSettings.ApiEndpoint, using the device-bootstrapped JWT.
        Api = 2
    }

    // Value == minutes, mirroring BackupInterval. Disabled means "manual sync only".
    public enum SyncInterval
    {
        Disabled = 0,
        FiveMinutes = 5,
        FifteenMinutes = 15,
        OneHour = 60
    }

    // WL-51. OncePerSession means once per application run, not once per RunSyncAsync call -
    // the point is to have *a* pre-merge snapshot to fall back on, and re-copying the database
    // on every sync cycle that happens to merge something costs more than it protects.
    public enum PreMergeBackupMode
    {
        Never = 0,
        OncePerSession = 1,
        Always = 2
    }

    public enum BackupInterval
    {
        Disabled = 0,
        OneMinute = 1,
        FiveMinutes = 5,
        OneHour = 60,

        // Legacy value: previously meant "only back up on close". Kept so old settings.json
        // files still deserialize; LoadSettings migrates it to BackupOnClose = true.
        OnClose = 999
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
        public static string GetDefaultDataDirectory()
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
                        DataDirectory = _dataDirectory,
                        ApplicationVersion = AppSettings.CurrentApplicationVersion,
                        DeviceId = Guid.NewGuid().ToString()
                    };
                    SaveSettings(defaults);
                    return defaults;
                }

                var json = File.ReadAllText(_settingsPath);
                var options = new JsonSerializerOptions();
                options.Converters.Add(new JsonStringEnumConverter());
                var settings = JsonSerializer.Deserialize<AppSettings>(json, options) ?? new AppSettings();

                // Backfill missing data directory and version for older settings files
                if (string.IsNullOrWhiteSpace(settings.DataDirectory))
                {
                    settings.DataDirectory = _dataDirectory;
                    SaveSettings(settings);
                }

                if (string.IsNullOrWhiteSpace(settings.ApplicationVersion))
                {
                    settings.ApplicationVersion = AppSettings.CurrentApplicationVersion;
                    SaveSettings(settings);
                }

                // Backfill backup settings if missing (for older settings files)
                if (string.IsNullOrWhiteSpace(settings.BackupDirectory))
                {
                    settings.BackupDirectory = string.Empty;
                    SaveSettings(settings);
                }

                // Migrate legacy "OnClose" interval to the standalone BackupOnClose flag
                if (settings.BackupInterval == BackupInterval.OnClose)
                {
                    settings.BackupOnClose = true;
                    settings.BackupInterval = BackupInterval.Disabled;
                    SaveSettings(settings);
                }

                // Mint and persist a stable per-install device ID for older settings files
                if (string.IsNullOrWhiteSpace(settings.DeviceId))
                {
                    settings.DeviceId = Guid.NewGuid().ToString();
                    SaveSettings(settings);
                }

                // Backfill sync folder path if missing (for older settings files)
                if (string.IsNullOrWhiteSpace(settings.SyncFolderPath))
                {
                    settings.SyncFolderPath = string.Empty;
                    SaveSettings(settings);
                }

                // Backfill API sync settings if missing (for older settings files). Only the
                // strings need this - the nullable DateTimes above survive a missing key on
                // their own, the same way SyncRetentionDays does.
                if (string.IsNullOrWhiteSpace(settings.ApiEndpoint))
                {
                    settings.ApiEndpoint = string.Empty;
                    SaveSettings(settings);
                }

                if (string.IsNullOrWhiteSpace(settings.ApiToken))
                {
                    settings.ApiToken = string.Empty;
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

        // Writers are no longer all on the UI thread: the sync timer persists the pull cursor
        // (and a renewed API token) through SettingsSyncStore while the settings screen's
        // debounced autosave may be writing the same file from its own task. Two concurrent
        // File.WriteAllText calls on one path throw, and the catch below would swallow it -
        // losing whichever save lost the race, silently.
        private static readonly object SaveLock = new();

        public void SaveSettings(AppSettings settings)
        {
            try
            {
                lock (SaveLock)
                {
                    WriteSettings(settings);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        private void WriteSettings(AppSettings settings)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                options.Converters.Add(new JsonStringEnumConverter());
                var json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Backs up settings.json and the sqlite database directly into the given destination directory,
        /// overwriting any files left there by a previous backup.
        /// Returns true if both files were successfully copied (or present and copied), false otherwise.
        /// </summary>
        public async Task<bool> CreateBackupAsync(string destinationDirectory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(destinationDirectory))
                    return false;

                // Ensure dest exists
                if (!Directory.Exists(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                var success = true;

                // Copy settings
                try
                {
                    var destSettings = Path.Combine(destinationDirectory, SettingsFileName);
                    await TryCopyFileWithRetriesAsync(_settingsPath, destSettings);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to backup settings: {ex.Message}");
                    success = false;
                }

                // Copy database
                try
                {
                    var srcDb = DatabasePath;
                    var destDb = Path.Combine(destinationDirectory, DefaultDbFileName);
                    await TryCopyFileWithRetriesAsync(srcDb, destDb);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to backup database: {ex.Message}");
                    success = false;
                }

                return success;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateBackupAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Validates that a folder looks like a backup source (contains settings.json and workledger.db)
        /// </summary>
        public bool ValidateBackupSource(string sourceDirectory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourceDirectory)) return false;
                var settingsPath = Path.Combine(sourceDirectory, SettingsFileName);
                var dbPath = Path.Combine(sourceDirectory, DefaultDbFileName);
                return File.Exists(settingsPath) && File.Exists(dbPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Validates that a folder is usable as a sync folder: unlike ValidateBackupSource,
        /// this doesn't need pre-existing content - it confirms the folder exists (or can be
        /// created) and is actually writable before FileSyncProvider is trusted to use it.
        /// </summary>
        public bool ValidateSyncFolder(string syncFolderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(syncFolderPath)) return false;

                if (!Directory.Exists(syncFolderPath))
                    Directory.CreateDirectory(syncFolderPath);

                var probePath = Path.Combine(syncFolderPath, $".workledger-sync-probe-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probePath, string.Empty);
                File.Delete(probePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restores settings.json and workledger.db from a backup source folder into the application's data directory.
        /// Existing files are preserved by renaming them with a .backup_TIMESTAMP suffix before overwrite.
        /// </summary>
        public async Task<(bool Success, string Error)> RestoreFromDirectoryAsync(string sourceDirectory)
        {
            try
            {
                if (!ValidateBackupSource(sourceDirectory))
                {
                    return (false, "Source directory does not contain required files.");
                }

                // Ensure data directory exists
                if (!Directory.Exists(_dataDirectory))
                    Directory.CreateDirectory(_dataDirectory);

                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

                // Backup existing settings
                var existingSettings = _settingsPath;
                if (File.Exists(existingSettings))
                {
                    var bak = existingSettings + $".backup_{timestamp}";
                    File.Copy(existingSettings, bak, overwrite: true);
                }

                // Backup existing DB
                var existingDb = DatabasePath;
                if (File.Exists(existingDb))
                {
                    var bakDb = existingDb + $".backup_{timestamp}";
                    File.Copy(existingDb, bakDb, overwrite: true);
                }

                // Copy new files
                var srcSettings = Path.Combine(sourceDirectory, SettingsFileName);
                var destSettings = _settingsPath;
                await TryCopyFileWithRetriesAsync(srcSettings, destSettings);

                var srcDb = Path.Combine(sourceDirectory, DefaultDbFileName);
                var destDb = DatabasePath;
                await TryCopyFileWithRetriesAsync(srcDb, destDb);

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RestoreFromDirectoryAsync failed: {ex.Message}");
                return (false, ex.Message);
            }
        }

        private async Task TryCopyFileWithRetriesAsync(string src, string dest, int maxAttempts = 3)
        {
            if (!File.Exists(src)) throw new FileNotFoundException("Source file not found", src);

            var attempt = 0;
            var lastEx = (Exception?)null;
            while (attempt < maxAttempts)
            {
                try
                {
                    File.Copy(src, dest, overwrite: true);
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    attempt++;
                    await Task.Delay(250);
                }
            }

            throw lastEx ?? new IOException("Unknown copy failure");
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
