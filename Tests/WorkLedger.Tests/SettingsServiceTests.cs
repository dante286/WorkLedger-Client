using System;
using System.IO;
using WorkLedger_Client.Services;
using Xunit;

namespace WorkLedger.Tests
{
    public class SettingsServiceTests : IDisposable
    {
        private readonly string _tempDataDirectory;

        public SettingsServiceTests()
        {
            _tempDataDirectory = Path.Combine(Path.GetTempPath(), "WorkLedgerTests_" + Guid.NewGuid());
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDataDirectory))
                Directory.Delete(_tempDataDirectory, recursive: true);
        }

        [Fact]
        public void LoadSettings_MigratesLegacyOnCloseInterval_ToBackupOnCloseFlag()
        {
            Directory.CreateDirectory(_tempDataDirectory);
            var legacySettingsPath = Path.Combine(_tempDataDirectory, SettingsService.SettingsFileName);
            File.WriteAllText(legacySettingsPath, """
                {
                    "Theme": "System",
                    "DataDirectory": "",
                    "ApplicationVersion": "1.0.0",
                    "BackupDirectory": "",
                    "BackupInterval": "OnClose",
                    "BackupOnClose": false
                }
                """);

            var service = new SettingsService(_tempDataDirectory);
            var settings = service.LoadSettings();

            Assert.True(settings.BackupOnClose);
            Assert.Equal(BackupInterval.Disabled, settings.BackupInterval);
        }

        [Fact]
        public void LoadSettings_LeavesNonLegacyInterval_Unchanged()
        {
            Directory.CreateDirectory(_tempDataDirectory);
            var settingsPath = Path.Combine(_tempDataDirectory, SettingsService.SettingsFileName);
            File.WriteAllText(settingsPath, """
                {
                    "Theme": "System",
                    "DataDirectory": "",
                    "ApplicationVersion": "1.0.0",
                    "BackupDirectory": "",
                    "BackupInterval": "FiveMinutes",
                    "BackupOnClose": false
                }
                """);

            var service = new SettingsService(_tempDataDirectory);
            var settings = service.LoadSettings();

            Assert.False(settings.BackupOnClose);
            Assert.Equal(BackupInterval.FiveMinutes, settings.BackupInterval);
        }

        [Fact]
        public void ValidateBackupSource_ReturnsTrue_WhenSettingsAndDatabaseFilesExist()
        {
            Directory.CreateDirectory(_tempDataDirectory);
            File.WriteAllText(Path.Combine(_tempDataDirectory, SettingsService.SettingsFileName), "{}");
            File.WriteAllText(Path.Combine(_tempDataDirectory, SettingsService.DefaultDbFileName), string.Empty);

            var service = new SettingsService(_tempDataDirectory);

            Assert.True(service.ValidateBackupSource(_tempDataDirectory));
        }

        [Fact]
        public void ValidateBackupSource_ReturnsFalse_WhenDatabaseFileMissing()
        {
            Directory.CreateDirectory(_tempDataDirectory);
            File.WriteAllText(Path.Combine(_tempDataDirectory, SettingsService.SettingsFileName), "{}");

            var service = new SettingsService(_tempDataDirectory);

            Assert.False(service.ValidateBackupSource(_tempDataDirectory));
        }

        [Fact]
        public void LoadSettings_OnFreshInstall_MintsDeviceId()
        {
            var service = new SettingsService(_tempDataDirectory);

            var settings = service.LoadSettings();

            Assert.False(string.IsNullOrWhiteSpace(settings.DeviceId));
        }

        [Fact]
        public void LoadSettings_BackfillsMissingDeviceId_ForOlderSettingsFiles()
        {
            Directory.CreateDirectory(_tempDataDirectory);
            var settingsPath = Path.Combine(_tempDataDirectory, SettingsService.SettingsFileName);
            File.WriteAllText(settingsPath, """
                {
                    "Theme": "System",
                    "DataDirectory": "",
                    "ApplicationVersion": "1.0.0",
                    "BackupDirectory": "",
                    "BackupInterval": "Disabled",
                    "BackupOnClose": false
                }
                """);

            var service = new SettingsService(_tempDataDirectory);

            var settings = service.LoadSettings();

            Assert.False(string.IsNullOrWhiteSpace(settings.DeviceId));
        }

        [Fact]
        public void LoadSettings_DeviceId_IsStableAcrossRepeatedLoads()
        {
            var service = new SettingsService(_tempDataDirectory);

            var first = service.LoadSettings();
            var second = service.LoadSettings();

            Assert.Equal(first.DeviceId, second.DeviceId);
        }

        [Fact]
        public void LoadSettings_BackfillsMissingSyncFolderPath_ForOlderSettingsFiles()
        {
            Directory.CreateDirectory(_tempDataDirectory);
            var settingsPath = Path.Combine(_tempDataDirectory, SettingsService.SettingsFileName);
            File.WriteAllText(settingsPath, """
                {
                    "Theme": "System",
                    "DataDirectory": "",
                    "ApplicationVersion": "1.0.0",
                    "BackupDirectory": "",
                    "BackupInterval": "Disabled",
                    "BackupOnClose": false
                }
                """);

            var service = new SettingsService(_tempDataDirectory);

            var settings = service.LoadSettings();

            Assert.Equal(string.Empty, settings.SyncFolderPath);
        }

        [Fact]
        public void ValidateSyncFolder_ReturnsTrue_WhenFolderExistsAndIsWritable()
        {
            Directory.CreateDirectory(_tempDataDirectory);
            var service = new SettingsService(_tempDataDirectory);

            Assert.True(service.ValidateSyncFolder(_tempDataDirectory));
        }

        [Fact]
        public void ValidateSyncFolder_CreatesFolder_WhenItDoesNotExistYet()
        {
            var service = new SettingsService(_tempDataDirectory);
            var syncFolder = Path.Combine(_tempDataDirectory, "sync-folder-not-created-yet");

            Assert.True(service.ValidateSyncFolder(syncFolder));
            Assert.True(Directory.Exists(syncFolder));
        }

        [Fact]
        public void ValidateSyncFolder_ReturnsFalse_WhenPathIsBlank()
        {
            var service = new SettingsService(_tempDataDirectory);

            Assert.False(service.ValidateSyncFolder(string.Empty));
        }

        [Fact]
        public void LoadSettings_OnFreshInstall_DefaultsSyncRetentionDaysTo30()
        {
            var service = new SettingsService(_tempDataDirectory);

            var settings = service.LoadSettings();

            Assert.Equal(30, settings.SyncRetentionDays);
        }

        [Fact]
        public void SaveAndLoadSettings_RoundTripsSyncRetentionDays()
        {
            var service = new SettingsService(_tempDataDirectory);
            var settings = service.LoadSettings();
            settings.SyncRetentionDays = 7;
            service.SaveSettings(settings);

            var reloaded = service.LoadSettings();

            Assert.Equal(7, reloaded.SyncRetentionDays);
        }

        [Fact]
        public void ValidateSyncFolder_ReturnsFalse_WhenPathIsBlockedByAFile()
        {
            Directory.CreateDirectory(_tempDataDirectory);
            var blockedPath = Path.Combine(_tempDataDirectory, "blocked-by-file");
            File.WriteAllText(blockedPath, "not a folder");

            var service = new SettingsService(_tempDataDirectory);

            Assert.False(service.ValidateSyncFolder(Path.Combine(blockedPath, "changes")));
        }

        [Fact]
        public void MigrateDataIfNeeded_CopiesDataBackToAPreviouslyUsedDirectory()
        {
            // Regression test for WL-74: migrating data was one-directional (old -> new only
            // ever meant "OS default -> custom"). Moving back to a directory that already has a
            // stale leftover copy from an earlier move must still pull across the real, current data.
            var dirA = _tempDataDirectory;
            var dirB = Path.Combine(Path.GetTempPath(), "WorkLedgerTests_" + Guid.NewGuid());

            try
            {
                Directory.CreateDirectory(dirA);
                File.WriteAllText(Path.Combine(dirA, SettingsService.SettingsFileName), "original-settings");
                File.WriteAllText(Path.Combine(dirA, SettingsService.DefaultDbFileName), "original-db");

                var service = new SettingsService(dirA);

                // Move dirA -> dirB (e.g. default -> custom). dirA's files are left behind, stale.
                Assert.True(service.MigrateDataIfNeeded(dirB, dirA));

                // Simulate the app being used at dirB afterwards, so its database evolves.
                File.WriteAllText(Path.Combine(dirB, SettingsService.DefaultDbFileName), "updated-db-after-use");

                // Move back dirB -> dirA, even though dirA still holds the stale pre-move copy.
                Assert.True(service.MigrateDataIfNeeded(dirA, dirB));

                Assert.Equal("updated-db-after-use", File.ReadAllText(Path.Combine(dirA, SettingsService.DefaultDbFileName)));
            }
            finally
            {
                if (Directory.Exists(dirB))
                    Directory.Delete(dirB, recursive: true);
            }
        }

        [Fact]
        public void GetDefaultDataDirectory_ReturnsAWorkLedgerPath()
        {
            var dir = SettingsService.GetDefaultDataDirectory();

            Assert.EndsWith("WorkLedger", dir);
        }
    }
}
