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
    }
}
