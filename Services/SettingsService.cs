using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WorkLedger_Client.Services
{
    public class AppSettings
    {
        // Kept in step with the tagged release by Release Please - the trailing annotation is
        // what its generic updater looks for, and release-please-config.json lists this file
        // under extra-files. Don't hand-edit it outside of a deliberate versioning reset.
        public const string CurrentApplicationVersion = "0.2.0"; // x-release-please-version

        public string Theme { get; set; } = "System"; // "System", "Light", "Dark"
        public string DataDirectory { get; set; } = string.Empty;
        public string ApplicationVersion { get; set; } = CurrentApplicationVersion;
        public string BackupDirectory { get; set; } = string.Empty;
        public BackupInterval BackupInterval { get; set; } = BackupInterval.Disabled;
        public bool BackupOnClose { get; set; } = false;
        // Stable per-install identifier, minted at first run. This is also the DeviceId
        // ApiSyncProvider signs in with and the OriginDeviceId stamped on every row this
        // install writes - there is deliberately only one of these.
        //
        // Note the server keys device rows by (UserId, DeviceId), so the same value under a
        // different user is a different device and inherits nothing from the old one.
        public string DeviceId { get; set; } = string.Empty;
        public string SyncFolderPath { get; set; } = string.Empty;

        // Base URL of the WorkLedger-API instance, e.g. "https://workledger.example.com".
        public string ApiEndpoint { get; set; } = string.Empty;

        // WL-135: the signed-in identity - prefill for the sign-in form, and the "signed in as"
        // the settings screen shows. Kept across a sign-out on purpose, so signing back in is
        // one field rather than two.
        //
        // The password is deliberately NOT here, and deliberately nowhere else either. DPAPI is
        // Windows-only and this ships three RIDs; a keyring is a dependency and a packaging
        // problem for a self-contained single-file publish; and "encrypted" next to its own key
        // is just obfuscation. Tokens last Jwt:ExpiryDays (365 by default), so "sign in once a
        // year, or when you've been signed out" is an honest cost. If an OS credential store is
        // wanted later it implements ApiSyncProvider's Func<ApiCredentials?> accessor and
        // nothing else changes - and Invariant L still bounds it to one unattended failed
        // attempt per credential, so remembering one cannot reintroduce the lockout storm.
        [JsonIgnore] // WL-148: account-scoped, so it lives in profile.json, never in settings.json.
        public string ApiEmail { get; set; } = string.Empty;

        // The JWT the *server* issued to this (user, device) pair via POST /auth/login - not a
        // hand-entered secret. Persisted so a restart doesn't force a fresh sign-in.
        [JsonIgnore] // WL-148: account-scoped, so it lives in profile.json, never in settings.json.
        public string ApiToken { get; set; } = string.Empty;

        // From LoginResponse.ExpiresAtUtc. Lets the app reach "your session has expired" without
        // making a request at all, which is what a year-idle install deserves instead of a
        // failed sync. Nullable: "never signed in" and "expired long ago" are different states
        // and DateTime.MinValue conflates them.
        //
        // Note this is an upper bound, not a guarantee: since the server's S7 the token also
        // carries a security stamp, so a password change, a sign-out-everywhere, or this device
        // being removed from another machine kills it well before this instant. That arrives as
        // a 401 and is handled as a revocation - see ApiSyncProvider.
        [JsonIgnore] // WL-148: account-scoped, so it lives in profile.json, never in settings.json.
        public DateTime? ApiTokenExpiresAtUtc { get; set; }

        // WL-92: the pull cursor, persisted so it survives a restart, and holding the value
        // the *provider* reported rather than this device's own clock. There is exactly one
        // "last synced" timestamp in this app by design - if a Sync settings section needs
        // something to display (WL-95), it displays this.
        [JsonIgnore] // WL-148: account-scoped, so it lives in profile.json, never in settings.json.
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

        // ApiSyncProvider against AppSettings.ApiEndpoint, using the JWT POST /auth/login
        // issued to this (user, device) pair.
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

    // WL-148: the account-scoped half of AppSettings, and the entire contents of a profile's
    // profile.json.
    //
    // These four were global fields holding per-account state, which is a conflation that
    // pre-dates profiles and bites without them: a restore, a data-directory move or a pre-merge
    // backup can already pair a token with a database it was never issued for. They are written to
    // exactly one file - hence [JsonIgnore] on the AppSettings properties - so there is never a
    // stale second copy of a token on disk to wonder about.
    internal class ProfileSettings
    {
        public string ApiEmail { get; set; } = string.Empty;

        public string ApiToken { get; set; } = string.Empty;

        public DateTime? ApiTokenExpiresAtUtc { get; set; }

        public DateTime? LastSyncedAtUtc { get; set; }
    }

    public class SettingsService
    {
        private readonly string _dataDirectory;
        private readonly string _settingsPath;

        // Not readonly, and the only mutable path in this class: a live profile switch re-points
        // it rather than rebuilding the service, so every holder of this instance - the view
        // model, the coordinator, SettingsSyncStore - keeps working without being rewired.
        private string _profileDirectory;

        public const string DefaultDbFileName = "workledger.db";
        public const string SettingsFileName = "settings.json";

        // WL-148. Sits beside the database it belongs to, so it travels with the rows through a
        // backup, a restore and a data-directory move - the same reasoning that put WL-138's
        // ownership stamp inside workledger.db rather than in settings.json.
        public const string ProfileSettingsFileName = "profile.json";

        /// <param name="profileDirectory">
        /// Where this profile's database and profile.json live. Defaults to the data directory
        /// itself, which is exactly the pre-WL-148 layout - so a SettingsService constructed with
        /// one argument still means what it has always meant.
        /// </param>
        public SettingsService(string? customDataDirectory = null, string? profileDirectory = null)
        {
            // Determine the data directory
            _dataDirectory = customDataDirectory ?? GetDefaultDataDirectory();

            // Ensure the data directory exists
            if (!Directory.Exists(_dataDirectory))
            {
                Directory.CreateDirectory(_dataDirectory);
            }

            _settingsPath = Path.Combine(_dataDirectory, SettingsFileName);

            _profileDirectory = profileDirectory ?? _dataDirectory;
            EnsureProfileDirectory();
        }

        /// <summary>
        /// Re-points this service at another profile's directory. WL-148.
        /// </summary>
        /// <remarks>
        /// Only <see cref="ProfileService.SwitchToAsync"/> calls this, and only with the app
        /// quiesced. The root settings.json does not move - it is device-scoped - so only the
        /// database path and profile.json follow.
        /// </remarks>
        public void UseProfile(string profileDirectory)
        {
            _profileDirectory = profileDirectory;
            EnsureProfileDirectory();
        }

        private void EnsureProfileDirectory()
        {
            try
            {
                if (!Directory.Exists(_profileDirectory))
                    Directory.CreateDirectory(_profileDirectory);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create profile directory: {ex.Message}");
            }
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

        // WL-148: the active profile's directory, which is the data directory itself in the
        // single-profile layout.
        public string ProfileDirectory => _profileDirectory;

        public string DatabasePath => Path.Combine(_profileDirectory, DefaultDbFileName);

        public string ProfileSettingsPath => Path.Combine(_profileDirectory, ProfileSettingsFileName);

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
                    // Overlaid BEFORE the save: a profile.json can outlive the root settings.json
                    // (a deleted settings file, a restore that carried only the profile), and a
                    // save that ran first would overwrite the session with blanks.
                    ApplyProfileSettings(defaults);
                    SaveSettings(defaults);
                    return defaults;
                }

                var json = File.ReadAllText(_settingsPath);
                var options = new JsonSerializerOptions();
                options.Converters.Add(new JsonStringEnumConverter());
                var settings = JsonSerializer.Deserialize<AppSettings>(json, options) ?? new AppSettings();

                // WL-148: the profile-scoped half, read straight after the root and BEFORE any of
                // the backfills below. Every one of those ends in a SaveSettings, which writes
                // profile.json too - so overlaying afterwards would mean each backfill blanked the
                // token and the cursor on its way past.
                ApplyProfileSettings(settings);

                // Backfill missing data directory and version for older settings files
                if (string.IsNullOrWhiteSpace(settings.DataDirectory))
                {
                    settings.DataDirectory = _dataDirectory;
                    SaveSettings(settings);
                }

                // Records which build last wrote this file, so it follows the running version
                // rather than only being filled in when absent. Left as a backfill-on-blank it
                // would pin the display to whatever version first created the file - which is
                // how a settings.json written by a v1.0.4 build kept reporting 1.0.4 after the
                // reset to the 0.1.x line.
                if (settings.ApplicationVersion != AppSettings.CurrentApplicationVersion)
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

                // WL-148: ApiToken, ApiTokenExpiresAtUtc, ApiEmail and LastSyncedAtUtc used to be
                // backfilled here. They are no longer in this file at all - they belong to the
                // profile, and were overlaid from profile.json above.
                return settings;
            }
            catch (Exception)
            {
                var fallback = new AppSettings
                {
                    DataDirectory = _dataDirectory
                };

                ApplyProfileSettings(fallback);
                return fallback;
            }
        }

        // WL-148. Reads the profile-scoped half over whatever the root settings.json produced.
        // A missing profile.json is the ordinary state of a profile nobody has signed in to, so it
        // leaves the (empty) initializer values alone rather than being treated as an error.
        private void ApplyProfileSettings(AppSettings settings)
        {
            var profile = LoadProfileSettings();

            settings.ApiEmail = profile.ApiEmail ?? string.Empty;
            settings.ApiToken = profile.ApiToken ?? string.Empty;
            settings.ApiTokenExpiresAtUtc = profile.ApiTokenExpiresAtUtc;
            settings.LastSyncedAtUtc = profile.LastSyncedAtUtc;
        }

        /// <summary>
        /// The account-scoped settings of the profile this service is pointed at. WL-148.
        /// </summary>
        /// <remarks>
        /// Internal rather than private because <see cref="ProfileService"/> reads and writes other
        /// profiles' files - notably to hand a token back to the profile being left behind during a
        /// switch - without re-pointing this service at them.
        /// </remarks>
        internal ProfileSettings LoadProfileSettings() => ReadProfileSettings(ProfileSettingsPath);

        internal static ProfileSettings ReadProfileSettings(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new ProfileSettings();

                var options = new JsonSerializerOptions();
                options.Converters.Add(new JsonStringEnumConverter());
                return JsonSerializer.Deserialize<ProfileSettings>(File.ReadAllText(path), options)
                       ?? new ProfileSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read profile settings: {ex.Message}");
                return new ProfileSettings();
            }
        }

        internal static void WriteProfileSettings(string path, ProfileSettings profile)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, JsonSerializer.Serialize(profile, options));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write profile settings: {ex.Message}");
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

                // WL-148: one save, two files. The four account-scoped fields are [JsonIgnore]d
                // out of the serialization above, so this is the only thing that persists them.
                WriteProfileSettings(ProfileSettingsPath, new ProfileSettings
                {
                    ApiEmail = settings.ApiEmail,
                    ApiToken = settings.ApiToken,
                    ApiTokenExpiresAtUtc = settings.ApiTokenExpiresAtUtc,
                    LastSyncedAtUtc = settings.LastSyncedAtUtc
                });
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

                // Copy database. WL-148: the ACTIVE profile's, and the only one - a backup covers
                // the profile you are working in, which is what the UI says it does.
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

                // Copy the profile's account-scoped settings alongside it, so a restored database
                // arrives with the session that belongs to it rather than next to a blank one.
                // Optional on both sides: a profile nobody has signed in to has no such file, and
                // a backup taken before WL-148 does not contain one.
                try
                {
                    if (File.Exists(ProfileSettingsPath))
                    {
                        await TryCopyFileWithRetriesAsync(
                            ProfileSettingsPath,
                            Path.Combine(destinationDirectory, ProfileSettingsFileName));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to backup profile settings: {ex.Message}");
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

                // WL-148: restoring into the ACTIVE profile, profile.json included when the backup
                // has one. If the restored database belongs to a different account, WL-138's stamp
                // travelled with it and ProfileService reconciles the registry entry from the stamp
                // the next time the profile is opened - the stamp is authoritative, the registry
                // entry is advisory.
                var srcProfile = Path.Combine(sourceDirectory, ProfileSettingsFileName);
                if (File.Exists(srcProfile))
                {
                    if (File.Exists(ProfileSettingsPath))
                        File.Copy(ProfileSettingsPath, ProfileSettingsPath + $".backup_{timestamp}", overwrite: true);

                    await TryCopyFileWithRetriesAsync(srcProfile, ProfileSettingsPath);
                }

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

                // Migrate every profile: profiles.json and the whole profiles/ tree. WL-148 -
                // the data directory holds ALL of this install's profiles, so moving it moves all
                // of them. Anything else would leave one account's work behind at the old path
                // with nothing pointing at it.
                var oldRegistry = Path.Combine(oldDir, ProfileService.RegistryFileName);
                if (File.Exists(oldRegistry))
                {
                    File.Copy(oldRegistry, Path.Combine(newDataDirectory, ProfileService.RegistryFileName), overwrite: true);
                    migratedAny = true;
                }

                var oldProfiles = Path.Combine(oldDir, ProfileService.ProfilesFolderName);
                if (Directory.Exists(oldProfiles))
                {
                    CopyDirectory(oldProfiles, Path.Combine(newDataDirectory, ProfileService.ProfilesFolderName));
                    migratedAny = true;
                }

                // Migrate a pre-WL-148 root database, for the one launch where a data-directory
                // move and the profile migration could race each other.
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

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

            foreach (var directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
