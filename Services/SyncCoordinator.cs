using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using WorkLedger_Client.Sync;

namespace WorkLedger_Client.Services
{
    // Owns the one live SyncOrchestrator and decides which ISyncProvider it drives.
    //
    // Plan 7 item 3/4. Everything below the ISyncProvider line - HttpClient lifetime, the
    // token store, the retention window, the pre-merge backup policy - is deliberately kept
    // out of MainWindowViewModel, which only needs "sync now" and "register this device".
    //
    // The AppSettings instance passed in is the *live* one the rest of the app holds, not a
    // fresh LoadSettings(). SettingsSyncStore writes the API token and the pull cursor
    // straight into it; if the settings screen were mutating a different copy, whichever one
    // saved last would silently drop the other's fields (which is exactly how a device would
    // lose its DeviceId and start looking like a brand new install).
    public class SyncCoordinator : IDisposable
    {
        private const string PreMergeBackupFolderName = "pre-merge-backups";

        // Enough to survive a couple of bad sync cycles without turning the data directory
        // into an unbounded copy of every merge that ever happened.
        private const int PreMergeBackupsToKeep = 5;

        private readonly LocalDbService _db;
        private readonly SettingsService _settingsService;
        private readonly AppSettings _settings;
        private readonly SettingsSyncStore _syncStore;

        // One HttpClient for the life of the app. ApiSyncProvider takes it rather than
        // constructing its own precisely so a provider rebuilt on every settings change
        // doesn't leak a socket pool each time.
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private SyncOrchestrator? _orchestrator;

        // Which settings the current orchestrator was built for, so ApplyConfiguration can
        // tell a real change from a no-op and avoid discarding a provider's warm state
        // (FileSyncProvider's "files I've already returned" set, notably) for nothing.
        private string _builtForConfiguration = string.Empty;

        // Held only for the duration of a RegisterDeviceAsync call - the bootstrap secret is
        // the operator's, mints the token, and is deliberately never persisted.
        private string? _bootstrapSecret;

        private bool _preMergeBackupTakenThisSession;

        public SyncCoordinator(LocalDbService db, SettingsService settingsService, AppSettings settings)
        {
            _db = db;
            _settingsService = settingsService;
            _settings = settings;
            _syncStore = new SettingsSyncStore(settingsService, settings);
        }

        // Re-raised from the orchestrator so a UI showing "last synced" updates after a
        // timer-driven run too, not just after the button the user pressed.
        public event EventHandler? SyncCompleted;

        public DateTime? LastSyncedAtUtc => _settings.LastSyncedAtUtc;

        /// <summary>
        /// Rebuilds the provider/orchestrator if the sync settings changed and starts, stops,
        /// or re-times the background tick to match. Safe to call on every settings save.
        /// </summary>
        public void ApplyConfiguration()
        {
            var problem = DescribeConfigurationProblem();
            if (problem != null)
            {
                // Misconfigured is not the same as off: the settings stay as the user typed
                // them, but there is nothing to tick against until they finish.
                _orchestrator?.StopSync();
                return;
            }

            EnsureOrchestrator();

            var runTimer = _settings.SyncMode != SyncMode.Off && _settings.SyncInterval != SyncInterval.Disabled;
            if (!runTimer)
            {
                _orchestrator!.StopSync();
                return;
            }

            // SyncInterval's value is minutes, the same "value == unit" convention
            // BackupInterval uses.
            _orchestrator!.ConfigureSyncInterval(TimeSpan.FromMinutes((int)_settings.SyncInterval));
        }

        /// <summary>
        /// Runs one push/pull/merge cycle now. Never throws: a configuration problem or an
        /// unreachable server comes back as a failed <see cref="SyncRunResult"/>, because the
        /// caller is a button on the settings screen.
        /// </summary>
        public async Task<SyncRunResult> SyncNowAsync()
        {
            var problem = DescribeConfigurationProblem();
            if (problem != null)
                return Failed(problem);

            try
            {
                EnsureOrchestrator();
                return await _orchestrator!.RunSyncAsync();
            }
            catch (Exception ex)
            {
                // ISyncProvider implementations are meant to be total, so this is the
                // belt-and-braces path - a sync must never take the app down.
                System.Diagnostics.Debug.WriteLine($"Sync run failed: {ex.Message}");
                return Failed(ex.Message);
            }
        }

        /// <summary>
        /// Registers this device with the configured WorkLedger-API instance and stores the
        /// JWT it issues. The secret is used for this call only and not persisted.
        /// </summary>
        public async Task<(bool Success, string Message)> RegisterDeviceAsync(string bootstrapSecret)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiEndpoint))
                return (false, "Enter an API endpoint first.");

            if (string.IsNullOrWhiteSpace(bootstrapSecret))
                return (false, "A bootstrap secret is required to register this device.");

            _bootstrapSecret = bootstrapSecret;
            try
            {
                // Built here rather than reused from the orchestrator so a device can be
                // registered while the mode is still being set up - registration is what
                // makes Api mode usable, so requiring it to already be selected and valid
                // would be the wrong way round.
                var provider = new ApiSyncProvider(
                    _http, _settings.ApiEndpoint, _settings.DeviceId, _syncStore, () => _bootstrapSecret, DeviceLabel());

                if (!await provider.TryBootstrapAsync())
                    return (false, "Registration failed - check the endpoint and bootstrap secret.");

                // TryBootstrapAsync persisted the token through SettingsSyncStore already.
                return (true, $"Device registered. Token valid until {_settings.ApiTokenExpiresAtUtc?.ToLocalTime():g}.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Device registration failed: {ex.Message}");
                return (false, $"Registration failed: {ex.Message}");
            }
            finally
            {
                _bootstrapSecret = null;
            }
        }

        public void Dispose()
        {
            _orchestrator?.StopSync();
            _http.Dispose();
        }

        // Null when the current settings can actually drive a sync, otherwise the reason they
        // can't - phrased for a toast, since that's where it ends up.
        private string? DescribeConfigurationProblem() => _settings.SyncMode switch
        {
            SyncMode.Off => "Sync is turned off.",
            SyncMode.File when string.IsNullOrWhiteSpace(_settings.SyncFolderPath) => "Choose a sync folder first.",
            SyncMode.Api when string.IsNullOrWhiteSpace(_settings.ApiEndpoint) => "Enter an API endpoint first.",
            SyncMode.Api when string.IsNullOrWhiteSpace(_settings.ApiToken) => "Register this device first.",
            _ => null
        };

        private void EnsureOrchestrator()
        {
            var configuration = string.Join(
                '|',
                _settings.SyncMode,
                _settings.SyncFolderPath,
                _settings.ApiEndpoint,
                _settings.SyncRetentionDays);

            if (_orchestrator != null && configuration == _builtForConfiguration)
                return;

            _orchestrator?.StopSync();

            var orchestrator = new SyncOrchestrator(_db, BuildProvider(), _syncStore, RunPreMergeBackupAsync);
            orchestrator.SyncCompleted += (_, e) => SyncCompleted?.Invoke(this, e);

            _orchestrator = orchestrator;
            _builtForConfiguration = configuration;
        }

        private ISyncProvider BuildProvider() => _settings.SyncMode switch
        {
            SyncMode.File => new FileSyncProvider(
                _settings.SyncFolderPath,
                _settings.DeviceId,
                TimeSpan.FromDays(Math.Max(1, _settings.SyncRetentionDays))),

            SyncMode.Api => new ApiSyncProvider(
                _http, _settings.ApiEndpoint, _settings.DeviceId, _syncStore, () => _bootstrapSecret, DeviceLabel()),

            _ => new NullSyncProvider()
        };

        // Purely cosmetic - the server shows it next to the device in its own tooling, and
        // the DeviceId is the identity that actually matters.
        private static string DeviceLabel()
        {
            try
            {
                return Environment.MachineName;
            }
            catch
            {
                return "WorkLedger desktop";
            }
        }

        // WL-51. The gate the orchestrator doesn't own: it knows *when* the first destructive
        // merge of a run is about to happen, this decides whether the user wanted a copy taken.
        // Read fresh from settings each time so changing the mode takes effect immediately
        // rather than at the next provider rebuild.
        private async Task RunPreMergeBackupAsync()
        {
            var mode = _settings.PreMergeBackupMode;
            if (mode == PreMergeBackupMode.Never)
                return;

            if (mode == PreMergeBackupMode.OncePerSession && _preMergeBackupTakenThisSession)
                return;

            _preMergeBackupTakenThisSession = true;

            // Deliberately not the user's BackupDirectory: CreateBackupAsync overwrites the
            // destination, so writing here would replace whatever scheduled backup was
            // sitting there with an automatic one the user never asked for. Timestamped
            // subfolders instead, pruned below, so a bad merge doesn't immediately overwrite
            // the snapshot taken before the merge that caused it.
            var root = Path.Combine(_settingsService.DataDirectory, PreMergeBackupFolderName);
            var destination = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));

            if (await _settingsService.CreateBackupAsync(destination))
                PruneOldPreMergeBackups(root);
        }

        private static void PruneOldPreMergeBackups(string root)
        {
            try
            {
                var stale = new DirectoryInfo(root)
                    .GetDirectories()
                    .OrderByDescending(directory => directory.Name, StringComparer.Ordinal)
                    .Skip(PreMergeBackupsToKeep);

                foreach (var directory in stale)
                    directory.Delete(recursive: true);
            }
            catch (Exception ex)
            {
                // Housekeeping only - a locked folder is not worth failing a sync over.
                System.Diagnostics.Debug.WriteLine($"Pruning pre-merge backups failed: {ex.Message}");
            }
        }

        private static SyncRunResult Failed(string message)
            => new() { Ran = false, Succeeded = false, ErrorMessage = message };
    }
}
