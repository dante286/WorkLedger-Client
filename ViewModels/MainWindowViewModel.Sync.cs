using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkLedger_Client.Services;

namespace WorkLedger_Client.ViewModels
{
    // The Settings tab's Sync section (plan 7 items 1-5). Everything that decides *how* to
    // sync lives in SyncCoordinator; this half only binds settings and reports outcomes.
    public partial class MainWindowViewModel
    {
        public ObservableCollection<SyncMode> SyncModeOptions { get; } = new()
        {
            SyncMode.Off,
            SyncMode.File,
            SyncMode.Api
        };

        public ObservableCollection<SyncInterval> SyncIntervalOptions { get; } = new()
        {
            SyncInterval.Disabled,
            SyncInterval.FiveMinutes,
            SyncInterval.FifteenMinutes,
            SyncInterval.OneHour
        };

        public ObservableCollection<PreMergeBackupMode> PreMergeBackupModeOptions { get; } = new()
        {
            PreMergeBackupMode.Never,
            PreMergeBackupMode.OncePerSession,
            PreMergeBackupMode.Always
        };

        [ObservableProperty]
        private SyncMode _syncMode = SyncMode.Off;

        [ObservableProperty]
        private SyncInterval _syncInterval = SyncInterval.FifteenMinutes;

        [ObservableProperty]
        private PreMergeBackupMode _preMergeBackupMode = PreMergeBackupMode.OncePerSession;

        [ObservableProperty]
        private string _apiEndpoint = string.Empty;

        // Read-only in the UI: this is the identity the server registers and the OriginDeviceId
        // stamped on every row this install writes. Editing it would orphan this device's
        // history and break self-echo suppression.
        [ObservableProperty]
        private string _deviceId = string.Empty;

        // Operator-supplied, used once by RegisterDeviceCommand, deliberately never persisted -
        // it's the gate on POST /auth/register-device, not this device's credential. Note the
        // absence of an autosave hook: PersistSettings has nothing to write for it.
        [ObservableProperty]
        private string _bootstrapSecret = string.Empty;

        [ObservableProperty]
        private string _lastSyncedDisplay = "Never";

        [ObservableProperty]
        private string _apiTokenStatus = "Not registered";

        [ObservableProperty]
        private bool _isSyncing;

        public bool IsFileSyncSelected => SyncMode == SyncMode.File;

        public bool IsApiSyncSelected => SyncMode == SyncMode.Api;

        public bool IsSyncConfigured => SyncMode != SyncMode.Off;

        [RelayCommand]
        private async Task SyncNowAsync()
        {
            if (IsSyncing)
                return;

            IsSyncing = true;
            try
            {
                // Anything typed but not yet autosaved (an endpoint, a freshly picked folder)
                // has to reach AppSettings before the coordinator builds a provider from it.
                PersistSettings();

                var result = await _syncCoordinator.SyncNowAsync();
                RefreshSyncStatus();

                if (!result.Ran && result.Succeeded)
                {
                    _toastService?.ShowToast("A sync is already running", 1600);
                }
                else if (!result.Succeeded)
                {
                    _toastService?.ShowToast($"Sync failed: {result.ErrorMessage}", 3500);
                }
                else
                {
                    _toastService?.ShowToast(
                        $"Synced · {result.PushedCount} sent, {result.ReceivedCount} received", 2000);
                }

                // A merge can have rewritten projects, tasks or entries underneath the open
                // view, so everything on screen is re-read rather than left showing pre-sync
                // state until the user clicks something.
                await ReloadAfterSyncAsync();
            }
            finally
            {
                IsSyncing = false;
            }
        }

        [RelayCommand]
        private async Task RegisterDeviceAsync()
        {
            // The endpoint the user just typed is what they expect to register against.
            PersistSettings();

            var (success, message) = await _syncCoordinator.RegisterDeviceAsync(BootstrapSecret);
            if (success)
            {
                // Held only for the round trip. Clearing it is what makes "prefer not
                // persisting it" true in practice rather than only in the settings file.
                BootstrapSecret = string.Empty;
            }

            RefreshSyncStatus();
            _toastService?.ShowToast(message, success ? 3000 : 4000);
        }

        // Re-reads the display-only sync state out of the live AppSettings. Cheap enough to
        // call after anything that might have moved the cursor or the token.
        private void RefreshSyncStatus()
        {
            LastSyncedDisplay = _settings.LastSyncedAtUtc is { } lastSynced
                ? lastSynced.ToLocalTime().ToString("g")
                : "Never";

            ApiTokenStatus = string.IsNullOrWhiteSpace(_settings.ApiToken)
                ? "Not registered"
                : _settings.ApiTokenExpiresAtUtc is { } expiry
                    ? $"Registered · token valid until {expiry.ToLocalTime():g}"
                    : "Registered";
        }

        // Fires on the sync timer's thread, not the UI's, so everything it touches is posted
        // back to the dispatcher - bound properties updated off-thread are the classic source
        // of intermittent Avalonia crashes.
        private void OnSyncCompleted(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                RefreshSyncStatus();
                _ = ReloadAfterSyncAsync();
            });
        }

        private async Task ReloadAfterSyncAsync()
        {
            try
            {
                LoadProjects();
                await LoadTasksAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Post-sync refresh failed: {ex.Message}");
            }
        }
    }
}
