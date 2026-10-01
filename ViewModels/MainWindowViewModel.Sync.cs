using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkLedger.Contracts.Auth;
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

        // WL-146 item 2a. Sits in the panel after the second failure, so it is already on screen
        // before the third attempt is sent - which is what "warn before sending" has to mean for a
        // button the user presses whenever they like.
        [ObservableProperty]
        private string _signInWarning = string.Empty;

        // Plan 9 item 2: while this is true the Sign in command refuses to run. The server rejects
        // even the correct password during a lockout, so an attempt could only burn the per-IP
        // limit shared with every other device on this network.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
        private bool _isLockedOut;

        [ObservableProperty]
        private string _lockoutCountdown = string.Empty;

        // WL-134 item 4e: the signed-out condition is persistent UI state, not an event. A skipped
        // run every 15 minutes is 96 a day, and toasting each one is 96 toasts - a correct design
        // that users would switch off. The toast fires only on the TRANSITION into it, tracked by
        // _lastAuthenticationRequired below; this is what stays on screen in between.
        [ObservableProperty]
        private bool _isSignedOutOfApi;

        // WL-176, plan 12 item 5: the panel's links to the server's web portal, which is where every
        // account operation lives now. Projected from AuthPanelState like everything else here.
        [ObservableProperty]
        private bool _showManageAccount;

        [ObservableProperty]
        private bool _showOpenPortal;

        [ObservableProperty]
        private string _portalHint = string.Empty;

        // Plan 12 item 6: "I've reset my password", visible exactly while a lockout disables Sign in.
        [ObservableProperty]
        private bool _showResetPasswordEscape;

        [ObservableProperty]
        private bool _isSyncing;

        // The previous value of IsSignedOutOfApi, so a toast can fire on the edge rather than the
        // level. Deliberately plain fields: they are not bound to anything and should not be.
        private bool _lastAuthenticationRequired;

        // What the server last said it allows. Null until probed, and null is a legitimate settled
        // answer too - a server predating /auth/capabilities, or one that is unreachable.
        private AuthCapabilitiesResponse? _capabilities;

        // The portal address from the last panel projection, exactly as the server advertised it.
        // Checked by PortalLink at the moment of launching, never before.
        private string? _portalUrl;

        // Ticks only while locked out, to run the countdown down and re-enable Sign in at the
        // instant the server would start accepting attempts again.
        private DispatcherTimer? _lockoutTimer;

        private DateTime? _lockedUntilUtc;

        // From GET /auth/whoami, so the panel can say "Signed in as A Person (a@example.com)". Not
        // persisted: it is cosmetic, and the email in settings is the identity that matters.
        private string? _signedInDisplayName;

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
                    // Plan 9 item 6. Not "Sync failed: 401" - a message whose only available
                    // response is to press Sync now again. The panel above now holds the
                    // explanation and the sign-in form with the email prefilled; this is the nudge
                    // towards it.
                    _toastService?.ShowToast(result.ErrorMessage ?? "Sign in to sync.", 4500);
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

        [RelayCommand(CanExecute = nameof(CanAttemptSignIn))]
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

                // WL-148: a sign-in can adopt this profile for the account that just signed in,
                // which renames it from "Local - not signed in" without any switch happening - so
                // the list is re-read here as well as after a switch.
                RefreshProfiles();
            }

            await RefreshAuthPanelAsync();
            _toastService?.ShowToast(message, success ? 3000 : 5000);
        }

        // Plan 9 item 2. Not a style choice: an attempt made during a lockout is refused by the
        // server whatever the password is, and spends the per-IP budget shared with every other
        // device behind this address.
        private bool CanAttemptSignIn() => !IsLockedOut;

        /// <summary>
        /// Opens the server's web portal in the system browser. WL-176, plan 12 item 5.
        /// </summary>
        /// <remarks>
        /// Behind both "Open web portal" and "Manage account": the portal's entry point is the only
        /// path the server promises, and it sends an anonymous browser to sign in and a signed-in one
        /// to account home. The system browser rather than an embedded one, because that is where
        /// password managers and existing sessions live.
        /// <para>
        /// The URL is the server's, so it passes <see cref="PortalLink.ToLaunchableUri"/> first and
        /// anything that fails is refused with a message - never handed to the OS shell, and never
        /// an exception.
        /// </para>
        /// </remarks>
        [RelayCommand]
        private async Task OpenPortalAsync()
        {
            var uri = PortalLink.ToLaunchableUri(_portalUrl);
            if (uri is null)
            {
                _toastService?.ShowToast(_portalUrl is null
                    ? "This server hasn't said where its web portal is."
                    : "This server's portal address isn't a secure web link, so it won't be opened.", 4500);
                return;
            }

            var desktop = Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

            if (desktop?.MainWindow is not { } window)
                return;

            try
            {
                if (!await window.Launcher.LaunchUriAsync(uri))
                    _toastService?.ShowToast($"Couldn't open a browser. The portal is at {uri}", 6000);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Opening the portal failed: {ex.Message}");
                _toastService?.ShowToast($"Couldn't open a browser. The portal is at {uri}", 6000);
            }
        }

        /// <summary>
        /// "I've reset my password". WL-176, plan 12 item 6. Sends nothing.
        /// </summary>
        /// <remarks>
        /// Only re-enables Sign in - see <c>SyncCoordinator.ForgetServerVerdict</c>. The re-render is
        /// <see cref="RefreshSyncStatus"/>, deliberately not <see cref="RefreshAuthPanelAsync"/>: the
        /// promise is that pressing this makes no request, and the next one the server sees is the
        /// user's own sign-in with the password they just chose.
        /// </remarks>
        [RelayCommand]
        private void ForgetLockout()
        {
            var forgotten = _syncCoordinator.ForgetServerVerdict();
            RefreshSyncStatus();

            if (forgotten)
                _toastService?.ShowToast("Sign in with your new password.", 3500);
        }

        // For any Settings action that needs a titled confirmation with its own wording. Returns
        // false when there is no window to parent a dialog to, which is the safe answer: no
        // confirmation, no action.
        private static async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

            if (desktop?.MainWindow is not { } owner)
                return false;

            var dialog = new Views.ConfirmationDialog(title, message, confirmLabel);
            return await dialog.ShowDialog<bool>(owner);
        }

        [RelayCommand]
        private Task RefreshCapabilitiesAsync() => RefreshAuthPanelAsync(refreshCapabilities: true);

        [RelayCommand]
        private async Task SignOutAsync()
        {
            // WL-138 item 6h / plan 9 item 7: an auth event, never a data event - and the message
            // says so, because "sign out" reads like "disconnect and forget" while this app's local
            // database is the user's actual work. It is also honest about what it does NOT do: the
            // token stops being used here but is not revoked server-side. Removing this device or
            // signing out everywhere is done in the server's web portal (plan 12).
            _syncCoordinator.SignOut();

            await RefreshAuthPanelAsync();

            _toastService?.ShowToast(
                "Signed out on this device. Your projects, tasks and entries stay here, and the "
                + "server hasn't been told to revoke the session.", 4500);
        }

        // Re-reads the display-only sync state out of the live AppSettings. Cheap enough to
        // call after anything that might have moved the cursor or the token.
        private void RefreshSyncStatus()
        {
            LastSyncedDisplay = _settings.LastSyncedAtUtc is { } lastSynced
                ? lastSynced.ToLocalTime().ToString("g")
                : "Never";

            ApplyAuthPanelState(AuthPanelState.From(
                _syncCoordinator.ApiAuth,
                _capabilities,
                _settings.ApiEmail,
                _signedInDisplayName,
                _settings.ApiTokenExpiresAtUtc));
        }

        /// <summary>
        /// WL-146. Re-probes what the server allows, refreshes who is signed in, and re-renders the
        /// panel. Call when the Sync settings come into view and after anything that could have
        /// changed the session.
        /// </summary>
        private async Task RefreshAuthPanelAsync(bool refreshCapabilities = false)
        {
            if (_settings.SyncMode == SyncMode.Api)
            {
                // Anonymous, so it works precisely when it is most needed - while signed out.
                _capabilities = await _syncCoordinator.GetCapabilitiesAsync(refreshCapabilities);

                // Cheap, authenticated, and it doubles as a liveness check: if the session has been
                // revoked elsewhere this is what finds out, at the cost of zero login attempts.
                if (_syncCoordinator.ApiAuth.CanSync)
                {
                    var whoami = await _syncCoordinator.WhoAmIAsync();
                    _signedInDisplayName = whoami?.DisplayName;
                }
                else
                {
                    _signedInDisplayName = null;
                }
            }

            RefreshSyncStatus();
        }

        private void ApplyAuthPanelState(AuthPanelState panel)
        {
            IsSignedOutOfApi = !panel.IsSignedIn;
            ApiTokenStatus = panel.StatusMessage;

            _portalUrl = panel.PortalUrl;
            ShowManageAccount = panel.ShowManageAccount;
            ShowOpenPortal = panel.ShowOpenPortal;
            PortalHint = panel.PortalHint;
            ShowResetPasswordEscape = panel.ShowResetPasswordEscape;

            SignInWarning = AuthPanelState.DescribeRepeatedFailureWarning(
                _syncCoordinator.ConsecutiveFailedSignIns, _capabilities);

            ApplyLockout(panel);

            // Plan 9 item 6: a revoked session belongs in the sign-in form with the email already
            // filled in, not behind a toast the user can only answer by pressing Sync now again.
            if (!panel.IsSignedIn && string.IsNullOrWhiteSpace(ApiEmail))
                ApiEmail = _settings.ApiEmail;

            // WL-134 item 4e: fire on the edge, not the level. A 15-minute tick that toasted every
            // time would be ~96 notifications a day; the state itself lives in the panel above.
            if (_authStateObserved && IsSignedOutOfApi && !_lastAuthenticationRequired && _settings.SyncMode == SyncMode.Api)
                _toastService?.ShowToast(panel.StatusMessage, 5000);

            _lastAuthenticationRequired = IsSignedOutOfApi;
            _authStateObserved = true;
        }

        private void ApplyLockout(AuthPanelState panel)
        {
            IsLockedOut = panel.IsLockedOut;
            _lockedUntilUtc = panel.LockedUntilUtc;
            LockoutCountdown = panel.IsLockedOut
                ? AuthPanelState.DescribeLockoutCountdown(_lockedUntilUtc, DateTime.UtcNow)
                : string.Empty;

            if (!panel.IsLockedOut)
            {
                _lockoutTimer?.Stop();
                return;
            }

            // Started lazily, and only while a lockout is actually running - a timer ticking for
            // the life of the app to service a state that is almost never true would be waste.
            _lockoutTimer ??= CreateLockoutTimer();
            _lockoutTimer.Start();
        }

        private DispatcherTimer CreateLockoutTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                LockoutCountdown = AuthPanelState.DescribeLockoutCountdown(_lockedUntilUtc, DateTime.UtcNow);
                if (!string.IsNullOrEmpty(LockoutCountdown))
                    return;

                // The wait is over. Re-read the auth state rather than just flipping the flag: the
                // provider clears an expired lockout itself, and this keeps the two in step.
                timer.Stop();
                RefreshSyncStatus();
            };

            return timer;
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
