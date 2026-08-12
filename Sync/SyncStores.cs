using System;
using WorkLedger_Client.Services;

namespace WorkLedger_Client.Sync
{
    // The device token ApiSyncProvider bootstraps and renews. Narrow on purpose: the
    // provider needs to read a token and write a new one, and nothing else about settings.
    // Passing SettingsService in instead would make every provider test touch settings.json.
    public interface IApiTokenStore
    {
        string? Token { get; }

        DateTime? ExpiresAtUtc { get; }

        void Save(string token, DateTime expiresAtUtc);
    }

    // WL-92: where the pull cursor lives between runs. Separate from IApiTokenStore because
    // it has a different owner - the orchestrator reads and writes this, the provider never
    // touches it.
    public interface ISyncCursorStore
    {
        DateTime? LastSyncedAtUtc { get; }

        void Save(DateTime cursor);
    }

    // Both stores backed by the app's real settings.json. Holds the loaded AppSettings that
    // App.axaml.cs already has rather than re-reading from disk on every access; writes go
    // straight through so a cursor or token survives a crash rather than only a clean exit.
    public class SettingsSyncStore : IApiTokenStore, ISyncCursorStore
    {
        private readonly SettingsService _settingsService;
        private readonly AppSettings _settings;

        public SettingsSyncStore(SettingsService settingsService, AppSettings settings)
        {
            _settingsService = settingsService;
            _settings = settings;
        }

        public string? Token => string.IsNullOrWhiteSpace(_settings.ApiToken) ? null : _settings.ApiToken;

        public DateTime? ExpiresAtUtc => _settings.ApiTokenExpiresAtUtc;

        public void Save(string token, DateTime expiresAtUtc)
        {
            _settings.ApiToken = token;
            _settings.ApiTokenExpiresAtUtc = expiresAtUtc;
            _settingsService.SaveSettings(_settings);
        }

        public DateTime? LastSyncedAtUtc => _settings.LastSyncedAtUtc;

        void ISyncCursorStore.Save(DateTime cursor)
        {
            _settings.LastSyncedAtUtc = cursor;
            _settingsService.SaveSettings(_settings);
        }
    }
}
