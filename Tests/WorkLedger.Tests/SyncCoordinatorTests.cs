using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using Xunit;

namespace WorkLedger.Tests
{
    // Plan 7 items 3-4/6: the piece that turns a SyncMode setting into a running sync. Two
    // devices are modelled the way SyncOrchestratorTests/FileSyncProviderTests do it - two
    // LocalDbService instances with different DeviceIds over different files - but driven
    // through the coordinator rather than a provider directly, so the settings -> provider ->
    // orchestrator wiring is what's under test.
    public class SyncCoordinatorTests : IAsyncLifetime
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"WorkLedgerCoordinatorTests_{Guid.NewGuid()}");

        private Device _alpha = null!;
        private Device _beta = null!;
        private string _sharedFolder = null!;

        public async Task InitializeAsync()
        {
            _sharedFolder = Path.Combine(_root, "shared");
            Directory.CreateDirectory(_sharedFolder);

            _alpha = await Device.CreateAsync(Path.Combine(_root, "alpha"), _sharedFolder);
            _beta = await Device.CreateAsync(Path.Combine(_root, "beta"), _sharedFolder);
        }

        public async Task DisposeAsync()
        {
            await _alpha.DisposeAsync();
            await _beta.DisposeAsync();
            TryDeleteDirectoryWithRetries(_root);
        }

        [Fact]
        public async Task SyncNowAsync_FileModeBetweenTwoDevices_PropagatesAProject()
        {
            await _alpha.Db.SaveProjectAsync(new Project { Name = "Shared project" });

            var pushed = await _alpha.Coordinator.SyncNowAsync();
            var pulled = await _beta.Coordinator.SyncNowAsync();

            Assert.True(pushed.Succeeded);
            Assert.True(pulled.Succeeded);
            Assert.Equal(1, pushed.PushedCount);

            var replicated = Assert.Single(await _beta.Db.GetAllAsync<Project>());
            Assert.Equal("Shared project", replicated.Name);
        }

        [Fact]
        public async Task SyncNowAsync_FileModeBothDirections_ConvergesWithoutDuplicating()
        {
            await _alpha.Db.SaveProjectAsync(new Project { Name = "From alpha" });
            await _beta.Db.SaveProjectAsync(new Project { Name = "From beta" });

            await _alpha.Coordinator.SyncNowAsync();
            await _beta.Coordinator.SyncNowAsync();
            await _alpha.Coordinator.SyncNowAsync();

            // A second round must not re-import what each device already merged - the version
            // comparison discards the replay rather than inserting it again.
            await _beta.Coordinator.SyncNowAsync();
            await _alpha.Coordinator.SyncNowAsync();

            Assert.Equal(
                new[] { "From alpha", "From beta" },
                (await _alpha.Db.GetAllAsync<Project>()).Select(p => p.Name).OrderBy(name => name).ToArray());
            Assert.Equal(
                new[] { "From alpha", "From beta" },
                (await _beta.Db.GetAllAsync<Project>()).Select(p => p.Name).OrderBy(name => name).ToArray());
        }

        [Fact]
        public async Task SyncNowAsync_ModeOff_ReportsThatRatherThanRunning()
        {
            _alpha.Settings.SyncMode = SyncMode.Off;

            var result = await _alpha.Coordinator.SyncNowAsync();

            Assert.False(result.Ran);
            Assert.False(result.Succeeded);
            Assert.Equal("Sync is turned off.", result.ErrorMessage);
        }

        [Fact]
        public async Task SyncNowAsync_FileModeWithNoFolderChosen_FailsWithAMessageInsteadOfThrowing()
        {
            _alpha.Settings.SyncFolderPath = string.Empty;

            var result = await _alpha.Coordinator.SyncNowAsync();

            Assert.False(result.Succeeded);
            Assert.Equal("Choose a sync folder first.", result.ErrorMessage);
        }

        [Fact]
        public async Task SyncNowAsync_ApiModeWithNoReachableServer_FailsGracefully()
        {
            // The acceptance criterion for exposing Api mode in the UI at all: a dead endpoint
            // must produce a message, not an unhandled exception.
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = "http://127.0.0.1:9";
            _alpha.Settings.ApiToken = "not-a-real-token";
            _alpha.Settings.ApiTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1);

            var result = await _alpha.Coordinator.SyncNowAsync();

            Assert.True(result.Ran);
            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public async Task RegisterDeviceAsync_WithoutAnEndpoint_AsksForOneInsteadOfCalling()
        {
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = string.Empty;

            var (success, message) = await _alpha.Coordinator.RegisterDeviceAsync("secret");

            Assert.False(success);
            Assert.Equal("Enter an API endpoint first.", message);
        }

        [Fact]
        public async Task SyncNowAsync_RemoteWinsOverwriteWithBackupsAlways_WritesASafetyCopy()
        {
            // WL-51 end to end: alpha's row is overwritten by beta's newer version, so the
            // configured pre-merge backup lands in alpha's data directory.
            _alpha.Settings.PreMergeBackupMode = PreMergeBackupMode.Always;

            var project = new Project { Name = "Original" };
            await _alpha.Db.SaveProjectAsync(project);
            await _alpha.Coordinator.SyncNowAsync();
            await _beta.Coordinator.SyncNowAsync();

            var onBeta = Assert.Single(await _beta.Db.GetAllAsync<Project>());
            onBeta.Name = "Edited on beta";
            await _beta.Db.SaveProjectAsync(onBeta);
            await _beta.Coordinator.SyncNowAsync();

            await _alpha.Coordinator.SyncNowAsync();

            Assert.Equal("Edited on beta", Assert.Single(await _alpha.Db.GetAllAsync<Project>()).Name);

            var backups = Directory.GetDirectories(Path.Combine(_alpha.DataDirectory, "pre-merge-backups"));
            var snapshot = Assert.Single(backups);
            Assert.True(File.Exists(Path.Combine(snapshot, SettingsService.DefaultDbFileName)));
        }

        [Fact]
        public async Task SyncNowAsync_PersistsTheCursorIntoSettings()
        {
            // WL-92's payoff: the cursor survives a restart because it lives in settings.json,
            // not in a field that resets to DateTime.MinValue on every launch.
            Assert.Null(_alpha.Settings.LastSyncedAtUtc);

            await _alpha.Coordinator.SyncNowAsync();

            Assert.NotNull(_alpha.Settings.LastSyncedAtUtc);
            Assert.Equal(_alpha.Settings.LastSyncedAtUtc, _alpha.Coordinator.LastSyncedAtUtc);

            var reloaded = new SettingsService(_alpha.DataDirectory).LoadSettings();
            Assert.NotNull(reloaded.LastSyncedAtUtc);
        }

        [Fact]
        public async Task PersistedSettings_KeepTheDeviceIdAndCursorAcrossSaves()
        {
            // The failure this guards against is silent: a save that rebuilds AppSettings from
            // only the fields a settings screen knows about blanks DeviceId, and the next load
            // mints a new one - at which point this install looks like a different device to
            // every peer and its own rows stop being recognised as self-echoes.
            var deviceId = _alpha.Settings.DeviceId;
            await _alpha.Coordinator.SyncNowAsync();

            _alpha.Settings.Theme = "Dark";
            _alpha.SettingsService.SaveSettings(_alpha.Settings);

            var reloaded = new SettingsService(_alpha.DataDirectory).LoadSettings();
            Assert.Equal(deviceId, reloaded.DeviceId);
            Assert.NotNull(reloaded.LastSyncedAtUtc);
            Assert.Equal(SyncMode.File, reloaded.SyncMode);
        }

        // One configured install: its own data directory, settings.json, database and
        // coordinator, all pointed at a sync folder shared with the other.
        private sealed class Device
        {
            public required SettingsService SettingsService { get; init; }
            public required AppSettings Settings { get; init; }
            public required LocalDbService Db { get; init; }
            public required SyncCoordinator Coordinator { get; init; }
            public required string DataDirectory { get; init; }

            public static async Task<Device> CreateAsync(string dataDirectory, string sharedFolder)
            {
                Directory.CreateDirectory(dataDirectory);

                var settingsService = new SettingsService(dataDirectory);
                var settings = settingsService.LoadSettings();
                settings.SyncMode = SyncMode.File;
                settings.SyncFolderPath = sharedFolder;
                settings.SyncInterval = SyncInterval.Disabled; // manual only: these tests drive it
                settings.PreMergeBackupMode = PreMergeBackupMode.Never;
                settingsService.SaveSettings(settings);

                var db = new LocalDbService(settingsService.DatabasePath, settings.DeviceId);
                await db.Initialization;

                return new Device
                {
                    SettingsService = settingsService,
                    Settings = settings,
                    Db = db,
                    Coordinator = new SyncCoordinator(db, settingsService, settings),
                    DataDirectory = dataDirectory
                };
            }

            public async Task DisposeAsync()
            {
                Coordinator.Dispose();
                await Db.Connection.CloseAsync();
            }
        }

        private static void TryDeleteDirectoryWithRetries(string path, int maxAttempts = 5)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
    }
}
