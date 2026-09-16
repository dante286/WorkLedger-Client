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

        // WL-146. The account-creation half of the panel, all of it driven by what the server said
        // it allows rather than by what this client can imagine offering.
        [ObservableProperty]
        private bool _canCreateAccount;

        [ObservableProperty]
        private bool _canClaimServer;

        [ObservableProperty]
        private bool _requiresSignupSecret;

        [ObservableProperty]
        private bool _isCreateAccountOpen;

        [ObservableProperty]
        private string _newAccountEmail = string.Empty;

        [ObservableProperty]
        private string _newAccountPassword = string.Empty;

        [ObservableProperty]
        private string _newAccountConfirmPassword = string.Empty;

        [ObservableProperty]
        private string _newAccountDisplayName = string.Empty;

        // The only place the bootstrap secret survives plan 8, and only while a registration form
        // is open. Never persisted - no autosave hook, and no AppSettings field to write it to.
        [ObservableProperty]
        private string _signupSecret = string.Empty;

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

        [ObservableProperty]
        private bool _isSyncing;

        // The previous value of IsSignedOutOfApi, so a toast can fire on the edge rather than the
        // level. Deliberately plain fields: they are not bound to anything and should not be.
        private bool _lastAuthenticationRequired;

        // What the server last said it allows. Null until probed, and null is a legitimate settled
        // answer too - a server predating /auth/capabilities, or one that is unreachable.
        private AuthCapabilitiesResponse? _capabilities;

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

        // WL-141. The account's devices, in the order the server sent them: most-recently-seen
        // first, never-seen last. Rebuilt wholesale on every refresh rather than merged, because
        // LastSeenUtc moves on every sync from every device and a stale row is a lie about where
        // someone is signed in.
        public ObservableCollection<AccountDeviceViewModel> Devices { get; } = new();

        // What to say instead of the list: "nothing asked yet", "couldn't reach the server", or the
        // reason there is nothing to show. Separate from an empty collection on purpose - a failed
        // request and an account with no devices are not the same fact.
        [ObservableProperty]
        private string _deviceListMessage = string.Empty;

        [ObservableProperty]
        private bool _isLoadingDevices;

        // WL-142. The change-password form, collapsed until asked for. Three bound fields, none of
        // them persisted and all three cleared the instant the call returns - the same discipline
        // the sign-in password is held to, for the same reason.
        [ObservableProperty]
        private bool _isChangePasswordOpen;

        [ObservableProperty]
        private string _currentPassword = string.Empty;

        [ObservableProperty]
        private string _newPassword = string.Empty;

        [ObservableProperty]
        private string _confirmNewPassword = string.Empty;

        // WL-143. The danger zone, collapsed by default and requiring two deliberate inputs once
        // opened. Neither field is persisted; both are cleared the moment the call returns.
        [ObservableProperty]
        private bool _isDangerZoneOpen;

        [ObservableProperty]
        private string _deleteAccountPassword = string.Empty;

        [ObservableProperty]
        private string _deleteAccountConfirmEmail = string.Empty;

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
                IsCreateAccountOpen = false;
            }

            await RefreshAuthPanelAsync();
            _toastService?.ShowToast(message, success ? 3000 : 5000);
        }

        // Plan 9 item 2. Not a style choice: an attempt made during a lockout is refused by the
        // server whatever the password is, and spends the per-IP budget shared with every other
        // device behind this address.
        private bool CanAttemptSignIn() => !IsLockedOut;

        [RelayCommand]
        private void ToggleCreateAccount()
        {
            IsCreateAccountOpen = !IsCreateAccountOpen;
            if (!IsCreateAccountOpen)
                ClearNewAccountForm();
        }

        [RelayCommand]
        private Task CreateAccountAsync() => SubmitNewAccountAsync(claiming: false);

        [RelayCommand]
        private Task ClaimServerAsync() => SubmitNewAccountAsync(claiming: true);

        // Registration and claim differ by one endpoint and by the wording around them; everything
        // else - validation, the automatic follow-on sign-in, clearing the form - is identical, so
        // they share this.
        private async Task SubmitNewAccountAsync(bool claiming)
        {
            PersistSettings();

            // Length only, and only against what this server reported. No composition rules of our
            // own: the server deliberately has none, and inventing one here would refuse passwords
            // it would have accepted.
            var invalid = AuthPanelState.ValidateNewPassword(
                NewAccountPassword, NewAccountConfirmPassword, _capabilities?.MinPasswordLength ?? 0);

            if (invalid != null)
            {
                _toastService?.ShowToast(invalid, 4000);
                return;
            }

            var email = NewAccountEmail;
            var password = NewAccountPassword;
            var displayName = string.IsNullOrWhiteSpace(NewAccountDisplayName) ? null : NewAccountDisplayName.Trim();
            var secret = string.IsNullOrWhiteSpace(SignupSecret) ? null : SignupSecret;

            // Out of the bound fields before the await, for the same reason the sign-in password is.
            NewAccountPassword = string.Empty;
            NewAccountConfirmPassword = string.Empty;
            SignupSecret = string.Empty;

            var (success, message) = claiming
                ? await _syncCoordinator.ClaimAndSignInAsync(email, password, displayName, secret)
                : await _syncCoordinator.RegisterAndSignInAsync(email, password, displayName, secret);

            if (success)
            {
                // The coordinator already signed in with these credentials, so there is nothing
                // left for this form to do.
                ApiEmail = _settings.ApiEmail;
                IsCreateAccountOpen = false;
                ClearNewAccountForm();
            }

            // Re-probe either way: a failure may have been the operator changing the server's mind
            // since we last asked, and a success has just consumed the only claimable account.
            await RefreshAuthPanelAsync(refreshCapabilities: true);
            _toastService?.ShowToast(message, success ? 3500 : 5000);
        }

        private void ClearNewAccountForm()
        {
            NewAccountPassword = string.Empty;
            NewAccountConfirmPassword = string.Empty;
            SignupSecret = string.Empty;
        }

        /// <summary>
        /// Re-reads the account's devices. WL-141.
        /// </summary>
        [RelayCommand]
        private async Task LoadDevicesAsync()
        {
            if (IsLoadingDevices)
                return;

            IsLoadingDevices = true;
            try
            {
                await ReloadDevicesAsync();
            }
            finally
            {
                IsLoadingDevices = false;
            }
        }

        /// <summary>
        /// Detaches one device from the account, after a confirmation that says what it does and
        /// does not do. WL-141.
        /// </summary>
        /// <remarks>
        /// The current device is offered like any other. It is permitted server-side and means
        /// "sign this app out" - see <see cref="AccountDeviceViewModel.ConfirmationMessage"/>, which
        /// says so in the dialog rather than leaving the button to surprise someone.
        /// </remarks>
        [RelayCommand]
        private async Task RemoveDeviceAsync(AccountDeviceViewModel? device)
        {
            if (device is null)
                return;

            var confirmed = await ConfirmAsync(
                "Remove device", device.ConfirmationMessage, "Remove device");

            if (!confirmed)
                return;

            var (success, message) = await _syncCoordinator.RemoveDeviceAsync(device.DeviceId);

            // Either way: a success changed the list, and a failure may have been a revocation that
            // changed the whole panel.
            await RefreshAuthPanelAsync();
            _toastService?.ShowToast(message, success ? 5000 : 4500);
        }

        // Shared by every destructive action in this panel. Returns false when there is no window
        // to parent a dialog to, which is the safe answer: no confirmation, no action.
        private static async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

            if (desktop?.MainWindow is not { } owner)
                return false;

            var dialog = new Views.ConfirmationDialog(title, message, confirmLabel);
            return await dialog.ShowDialog<bool>(owner);
        }

        // Pulls the device list fresh. Signed out there is nothing to ask for and no token to ask
        // with, so the list is emptied rather than left showing the previous session's devices.
        private async Task ReloadDevicesAsync()
        {
            if (_settings.SyncMode != SyncMode.Api || !_syncCoordinator.ApiAuth.CanSync)
            {
                Devices.Clear();
                DeviceListMessage = string.Empty;
                return;
            }

            var outcome = await _syncCoordinator.GetDevicesAsync();

            Devices.Clear();
            if (!outcome.Success)
            {
                DeviceListMessage = outcome.ErrorMessage ?? "Couldn't load this account's devices.";
                return;
            }

            foreach (var device in outcome.Devices)
                Devices.Add(new AccountDeviceViewModel(device));

            // An account always has at least the device asking, so an empty list here means the
            // server answered with something unexpected rather than "you have no devices".
            DeviceListMessage = Devices.Count == 0
                ? "The server reported no devices for this account."
                : string.Empty;
        }

        [RelayCommand]
        private void ToggleChangePassword()
        {
            IsChangePasswordOpen = !IsChangePasswordOpen;
            ClearPasswordForm();
        }

        /// <summary>
        /// Changes the account password. WL-142.
        /// </summary>
        /// <remarks>
        /// Validated against the server's own <c>MinPasswordLength</c> from the cached capabilities
        /// and nothing else - the server deliberately has no composition rules (NIST 800-63B:
        /// length is the control that matters), and a client demanding a symbol would refuse a
        /// password the server would have taken.
        /// <para>
        /// A wrong current password comes back as a message and leaves the session alone. It is
        /// emphatically <b>not</b> the revoked-session path: see <c>credentialGuarded</c> in
        /// <c>ApiSyncProvider</c>, which is what keeps the two 401s apart.
        /// </para>
        /// </remarks>
        [RelayCommand]
        private async Task SubmitPasswordChangeAsync()
        {
            var invalid = AuthPanelState.ValidateNewPassword(
                NewPassword, ConfirmNewPassword, _capabilities?.MinPasswordLength ?? 0);

            if (invalid != null)
            {
                _toastService?.ShowToast(invalid, 4000);
                return;
            }

            var current = CurrentPassword;
            var next = NewPassword;

            // Out of the bound fields before the await: they are attached to controls the user can
            // still see, and no outcome makes leaving them there the right answer.
            ClearPasswordForm();

            var (success, message) = await _syncCoordinator.ChangePasswordAsync(current, next);
            if (success)
                IsChangePasswordOpen = false;

            await RefreshAuthPanelAsync();
            _toastService?.ShowToast(message, success ? 5000 : 4500);
        }

        /// <summary>
        /// Signs every device out, keeping this one signed in. WL-142.
        /// </summary>
        /// <remarks>
        /// Deliberately worded to contrast with the local <c>Sign out</c> beside it - that one drops
        /// this app's token and tells the server nothing; this one kills every token the account
        /// has and takes a replacement for this device. Both leave local data alone, and both say
        /// so, because "sign out" is the phrase people most often read as "and delete my stuff".
        /// </remarks>
        [RelayCommand]
        private async Task SignOutEverywhereAsync()
        {
            var confirmed = await ConfirmAsync(
                "Sign out everywhere",
                "Sign out every device on this account?\n\n"
                + "Every other device will need to sign in again with your password. This one stays "
                + "signed in - the server issues it a fresh token as part of the same request.\n\n"
                + "Nothing is deleted anywhere: your projects, tasks and time entries stay on the "
                + "account and on every device that holds them.",
                "Sign out everywhere");

            if (!confirmed)
                return;

            var (success, message) = await _syncCoordinator.SignOutEverywhereAsync();

            await RefreshAuthPanelAsync();
            _toastService?.ShowToast(message, success ? 5000 : 4500);
        }

        private void ClearPasswordForm()
        {
            CurrentPassword = string.Empty;
            NewPassword = string.Empty;
            ConfirmNewPassword = string.Empty;
        }

        [RelayCommand]
        private void ToggleDangerZone()
        {
            IsDangerZoneOpen = !IsDangerZoneOpen;
            ClearDeleteAccountForm();
        }

        /// <summary>
        /// Deletes the account on the server. WL-143. <b>Immediate, total, irreversible.</b>
        /// </summary>
        /// <remarks>
        /// Three gates before anything is sent, deliberately: the section is collapsed, the email
        /// has to be typed to match, and the confirmation has to be accepted. The email is checked
        /// <i>locally first</i> - the server checks it too, but only after it has checked the
        /// password, so letting a typo through would spend one of the five attempts standing
        /// between this user and a lockout for a mistake this client can see.
        /// <para>
        /// On success the session is stood down and sync is switched off by the coordinator; the
        /// two lines below are this half of it, keeping the bound properties in step so the next
        /// autosave does not write the deleted account's mode and address straight back.
        /// </para>
        /// <para>
        /// On failure <b>nothing changes</b> - not the token, not the mode, not a single local row.
        /// A failed guard leaves the account fully intact server-side, so anything cleared here
        /// would invent the problem the guard just prevented.
        /// </para>
        /// </remarks>
        [RelayCommand]
        private async Task DeleteAccountAsync()
        {
            var confirmed = await ConfirmAsync(
                "Delete account",
                AuthPanelState.DescribeAccountDeletion(_settings.ApiEmail),
                "Delete permanently");

            if (!confirmed)
                return;

            var password = DeleteAccountPassword;
            var confirmEmail = DeleteAccountConfirmEmail;
            ClearDeleteAccountForm();

            var (success, message) = await _syncCoordinator.DeleteAccountAsync(password, confirmEmail);
            if (success)
            {
                // The coordinator has already written both to settings. Mirroring them into the
                // bound properties is what stops the next autosave - which rebuilds settings from
                // these - putting the deleted account's email and Api mode back.
                ApiEmail = string.Empty;
                SyncMode = SyncMode.Off;

                IsDangerZoneOpen = false;
                IsChangePasswordOpen = false;
            }

            await RefreshAuthPanelAsync();

            // Long, because it is the only place the user is told their local work survived.
            _toastService?.ShowToast(message, success ? 9000 : 5000);
        }

        private void ClearDeleteAccountForm()
        {
            DeleteAccountPassword = string.Empty;
            DeleteAccountConfirmEmail = string.Empty;
        }

        [RelayCommand]
        private Task RefreshCapabilitiesAsync() => RefreshAuthPanelAsync(refreshCapabilities: true);

        [RelayCommand]
        private async Task SignOutAsync()
        {
            // WL-138 item 6h / plan 9 item 7: an auth event, never a data event - and the message
            // says so, because "sign out" reads like "disconnect and forget" while this app's local
            // database is the user's actual work. It is also honest about what it does NOT do: the
            // token stops being used here but is not revoked server-side. That is logout-all, and
            // it is plan 10.
            _syncCoordinator.SignOut();

            // Nothing in the signed-in half of the panel applies any more, and a half-typed
            // password left in a collapsed form is the kind of thing that surprises someone later.
            IsChangePasswordOpen = false;
            ClearPasswordForm();
            IsDangerZoneOpen = false;
            ClearDeleteAccountForm();

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

                // WL-141. After whoami, so a session revoked elsewhere has already been discovered
                // and this doesn't spend a second request finding the same 401 out again.
                await ReloadDevicesAsync();
            }

            RefreshSyncStatus();
        }

        private void ApplyAuthPanelState(AuthPanelState panel)
        {
            IsSignedOutOfApi = !panel.IsSignedIn;
            ApiTokenStatus = panel.StatusMessage;
            CanCreateAccount = panel.ShowCreateAccount;
            CanClaimServer = panel.ShowClaimServer;
            RequiresSignupSecret = panel.ShowSignupSecret;

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
