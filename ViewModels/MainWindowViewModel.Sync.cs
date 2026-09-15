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

        // WL-135: the signed-in identity. This one IS persisted - it prefills the form and is the
        // "signed in as" the panel shows - and it survives a sign-out on purpose.
        [ObservableProperty]
        private string _apiEmail = string.Empty;

        // WL-135: used once by SignInCommand and cleared the instant the call returns, success or
        // failure. Never persisted anywhere - note the absence of an autosave hook, and see
        // AppSettings.ApiEmail for why storing it was considered and rejected for v1.
        [ObservableProperty]
        private string _apiPassword = string.Empty;

        [ObservableProperty]
        private string _lastSyncedDisplay = "Never";

        [ObservableProperty]
        private string _apiTokenStatus = "Not signed in";

        // WL-134 item 4e: the signed-out condition is persistent UI state, not an event. A skipped
        // run every 15 minutes is 96 a day, and toasting each one is 96 toasts - a correct design
        // that users would switch off. The toast fires only on the TRANSITION into it, tracked by
        // _lastAuthenticationRequired below; this is what stays on screen in between.
        [ObservableProperty]
        private bool _isSignedOutOfApi;

        [ObservableProperty]
        private bool _isSyncing;

        // The previous value of IsSignedOutOfApi, so a toast can fire on the edge rather than the
        // level. Deliberately plain fields: they are not bound to anything and should not be.
        private bool _lastAuthenticationRequired;

        // Launching into a signed-out state is not a transition into one. Without this, an install
        // configured for Api sync greets the user with a toast on every start, which is the
        // behaviour item 4e exists to avoid - just once a session instead of once a tick.
        private bool _authStateObserved;

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
                else if (result.AuthenticationRequired)
                {
                    // Already carries the coordinator's specific wording - signed out, expired,
                    // revoked, locked out or rate-limited - so it is shown as-is rather than
                    // wrapped in "Sync failed", which is the framing that sends people looking for
                    // a network problem they don't have.
                    _toastService?.ShowToast(result.ErrorMessage ?? "Sign in to sync.", 4000);
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
        private async Task SignInAsync()
        {
            // The endpoint and email the user just typed are what they expect to sign in with.
            PersistSettings();

            var password = ApiPassword;

            // Cleared before the await, not after it: the field is bound to a control the user can
            // still see, and there is no outcome - success, wrong password, unreachable server -
            // in which leaving the password sitting in it is the right answer.
            ApiPassword = string.Empty;

            var (success, message) = await _syncCoordinator.SignInAsync(ApiEmail, password);
            if (success)
            {
                // The coordinator normalizes and persists the address the server actually
                // recognized, which may differ in case from what was typed.
                ApiEmail = _settings.ApiEmail;
            }

            RefreshSyncStatus();
            _toastService?.ShowToast(message, success ? 3000 : 5000);
        }

        [RelayCommand]
        private void SignOut()
        {
            // WL-138 item 6h: an auth event, never a data event. The database, the pending queue,
            // the cursor and the ownership stamp all survive - the app has to stay fully usable
            // with no server, so signing out is not a request to stop working.
            _syncCoordinator.SignOut();
            RefreshSyncStatus();
            _toastService?.ShowToast("Signed out. Your work stays on this device.", 3000);
        }

        // Re-reads the display-only sync state out of the live AppSettings. Cheap enough to
        // call after anything that might have moved the cursor or the token.
        private void RefreshSyncStatus()
        {
            LastSyncedDisplay = _settings.LastSyncedAtUtc is { } lastSynced
                ? lastSynced.ToLocalTime().ToString("g")
                : "Never";

            var auth = _syncCoordinator.ApiAuth;
            IsSignedOutOfApi = !auth.CanSync;

            ApiTokenStatus = auth.CanSync
                ? DescribeSignedIn()
                : _syncCoordinator.DescribeAuthState();

            // WL-134 item 4e: fire on the edge, not the level.
            if (_authStateObserved && IsSignedOutOfApi && !_lastAuthenticationRequired && _settings.SyncMode == SyncMode.Api)
                _toastService?.ShowToast(ApiTokenStatus, 4000);

            _lastAuthenticationRequired = IsSignedOutOfApi;
            _authStateObserved = true;
        }

        private string DescribeSignedIn()
        {
            var who = string.IsNullOrWhiteSpace(_settings.ApiEmail) ? "Signed in" : $"Signed in as {_settings.ApiEmail}";

            // The expiry is an upper bound rather than a promise - the server can revoke the token
            // long before it - so it is shown as context next to who is signed in, not as a
            // countdown to anything the user has to act on.
            return _settings.ApiTokenExpiresAtUtc is { } expiry
                ? $"{who} · session valid until {expiry.ToLocalTime():g}"
                : who;
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
                await LoadWeeklyReportAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Post-sync refresh failed: {ex.Message}");
            }
        }
    }
}
