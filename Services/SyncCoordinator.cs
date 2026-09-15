using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using WorkLedger.Contracts.Auth;
using WorkLedger_Client.Models;
using WorkLedger_Client.Sync;

namespace WorkLedger_Client.Services
{
    // Owns the one live SyncOrchestrator and decides which ISyncProvider it drives.
    //
    // Plan 7 item 3/4. Everything below the ISyncProvider line - HttpClient lifetime, the
    // token store, the retention window, the pre-merge backup policy - is deliberately kept
    // out of MainWindowViewModel, which only needs "sync now" and "sign in".
    //
    // WL-137/WL-138 (plan 8). This is also where signing in happens, and therefore where the
    // account-ownership guard lives: SignInAsync is the only moment at which this app learns
    // which user it is talking to, and the only moment at which refusing is still free. See
    // Models/DatabaseOwner for the hazard it exists to prevent.
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
        private readonly HttpClient _http;

        // Only dispose what we made. A caller-supplied client (tests, scripting a fake server
        // through an HttpMessageHandler) belongs to the caller and may well outlive this.
        private readonly bool _ownsHttpClient;

        private SyncOrchestrator? _orchestrator;

        // Which settings the current orchestrator was built for, so ApplyConfiguration can
        // tell a real change from a no-op and avoid discarding a provider's warm state
        // (FileSyncProvider's "files I've already returned" set, notably) for nothing.
        private string _builtForConfiguration = string.Empty;

        // Held only for the duration of a SignInAsync call, and the only place a password exists
        // in this process at all. Never persisted, never logged, and cleared in a finally so an
        // exception cannot leave one resident.
        //
        // The provider reads it through a closure over this field rather than being handed a
        // string, which is what makes "the app is not holding a password right now" - true for
        // all but a second or two of its life - something the type system can express.
        private ApiCredentials? _credential;

        // THE Api provider - one instance, shared by the orchestrator's sync calls and by
        // everything the auth panel does. Deliberately not two.
        //
        // Two would let the timer discover a revocation that the UI never hears about, because the
        // 401 verdict lives on the instance that saw it. It would also probe /auth/capabilities
        // twice. Keyed by endpoint, so pointing at a different server gets a genuinely fresh
        // session and capability cache; not cleared when the mode changes, since the token in
        // settings is the same either way and the capability cache is worth keeping across a toggle.
        private ApiSyncProvider? _apiProvider;
        private string _apiProviderEndpoint = string.Empty;

        // WL-146, plan 9 item 2a. Consecutive failed INTERACTIVE sign-ins for the current email,
        // held in memory only.
        //
        // The number is deliberately a lower bound rather than the truth: the server's
        // FailedLoginCount lives on the USER row, so attempts from a phone or another laptop spend
        // the same budget and this install cannot see them. That is why the warning fires at three
        // rather than at the threshold - being conservative is the only honest option when the
        // count you hold is not the count that matters.
        private int _consecutiveFailedSignIns;
        private string _failureCountEmail = string.Empty;

        private bool _preMergeBackupTakenThisSession;

        // httpClient is injectable purely so the sign-in path - and with it WL-138's ownership
        // guard, which can only run after a real login response - is reachable from a test
        // without a server. The app passes nothing and gets the one long-lived client.
        public SyncCoordinator(
            LocalDbService db,
            SettingsService settingsService,
            AppSettings settings,
            HttpClient? httpClient = null)
        {
            _db = db;
            _settingsService = settingsService;
            _settings = settings;
            _syncStore = new SettingsSyncStore(settingsService, settings);
            _ownsHttpClient = httpClient is null;
            _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        // Re-raised from the orchestrator so a UI showing "last synced" updates after a
        // timer-driven run too, not just after the button the user pressed.
        public event EventHandler? SyncCompleted;

        public DateTime? LastSyncedAtUtc => _settings.LastSyncedAtUtc;

        /// <summary>
        /// The API session as it currently stands. WL-134 item 4a.
        /// </summary>
        /// <remarks>
        /// Falls back to deriving the state from the stored token when no provider has been built
        /// - the mode may be Api with the endpoint still half-typed, and "you are signed out" is
        /// a truthful and useful thing to say about that.
        /// </remarks>
        public AuthStatus ApiAuth
        {
            get
            {
                if (_apiProvider is { } provider)
                    return provider.Auth;

                if (string.IsNullOrWhiteSpace(_settings.ApiToken))
                    return AuthStatus.SignedOut;

                return _settings.ApiTokenExpiresAtUtc is { } expiry && expiry <= DateTime.UtcNow
                    ? AuthStatus.SessionExpired
                    : AuthStatus.SignedIn;
            }
        }

        /// <summary>
        /// WL-137: which kind of "no" this is, in words. Five states, five messages.
        /// </summary>
        /// <remarks>
        /// They are kept distinct because they ask for five different things, and collapsing any
        /// two of them costs the user a guess. In particular "sign in to sync" and "you have been
        /// signed out" look similar and are not: the second means a session that WAS working has
        /// been killed elsewhere, and reading it as an ordinary sync failure means retrying
        /// forever against a token that will never work again.
        /// </remarks>
        public string DescribeAuthState()
        {
            var auth = ApiAuth;
            return auth.State switch
            {
                AuthState.SignedOut => "Sign in to sync.",
                AuthState.SessionExpired => "Your session has expired - sign in again.",
                AuthState.SessionRevoked => "You've been signed out - sign in again.",
                AuthState.LockedOut => DescribeWait("Too many failed sign-in attempts", auth.RetryAfterUtc),
                AuthState.RateLimited => DescribeWait("The server is rate-limiting sign-in attempts", auth.RetryAfterUtc),
                _ => "Signed in."
            };
        }

        private static string DescribeWait(string what, DateTime? until)
        {
            if (until is not { } instant)
                return what + ". Try again shortly.";

            var minutes = Math.Max(1, (int)Math.Ceiling((instant - DateTime.UtcNow).TotalMinutes));
            return $"{what}. Try again in about {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
        }

        /// <summary>
        /// Consecutive failed interactive sign-ins seen by this install for the current email.
        /// WL-146 item 2a. A lower bound on the server's count, never the truth - see the field.
        /// </summary>
        public int ConsecutiveFailedSignIns => _consecutiveFailedSignIns;

        /// <summary>
        /// What the configured server allows, or null when it cannot say. WL-146 item 1.
        /// </summary>
        /// <remarks>
        /// Null is a normal answer, not an error: an endpoint that isn't set yet, a server predating
        /// the capabilities endpoint, or one that is simply unreachable right now. Every caller
        /// treats null as "sign-in only", which keeps a pre-user-accounts server usable.
        /// </remarks>
        public Task<AuthCapabilitiesResponse?> GetCapabilitiesAsync(bool refresh = false)
        {
            var provider = EnsureApiProvider();
            return provider is null
                ? Task.FromResult<AuthCapabilitiesResponse?>(null)
                : provider.TryGetCapabilitiesAsync(refresh);
        }

        /// <summary>
        /// Who this token belongs to, straight from the server. WL-146 item 5.
        /// </summary>
        /// <remarks>
        /// Doubles as a liveness check: it is the cheapest authenticated request there is, so asking
        /// it when the panel opens confirms the session is still real without costing a login
        /// attempt. Null when there is no session or the server cannot be reached.
        /// </remarks>
        public Task<WhoAmIResponse?> WhoAmIAsync()
        {
            var provider = EnsureApiProvider();
            return provider is null ? Task.FromResult<WhoAmIResponse?>(null) : provider.TryWhoAmIAsync();
        }

        /// <summary>
        /// Creates an account and signs in with it. WL-146 item 3.
        /// </summary>
        public Task<(bool Success, string Message)> RegisterAndSignInAsync(
            string email, string password, string? displayName, string? bootstrapSecret)
            => CreateAccountAndSignInAsync(email, password, displayName, bootstrapSecret, claiming: false);

        /// <summary>
        /// Takes ownership of this server's pre-existing unclaimed data, then signs in. WL-146 item 4.
        /// </summary>
        public Task<(bool Success, string Message)> ClaimAndSignInAsync(
            string email, string password, string? displayName, string? bootstrapSecret)
            => CreateAccountAndSignInAsync(email, password, displayName, bootstrapSecret, claiming: true);

        private async Task<(bool Success, string Message)> CreateAccountAndSignInAsync(
            string email, string password, string? displayName, string? bootstrapSecret, bool claiming)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiEndpoint))
                return (false, "Enter an API endpoint first.");

            var provider = EnsureApiProvider();
            if (provider is null)
                return (false, "Enter an API endpoint first.");

            try
            {
                var outcome = claiming
                    ? await provider.TryClaimAsync(email.Trim(), password, displayName, bootstrapSecret)
                    : await provider.TryRegisterAsync(email.Trim(), password, displayName, bootstrapSecret);

                if (!outcome.Success)
                {
                    // Re-probe before answering, so the panel the user is looking at stops offering
                    // whatever just turned out to be impossible.
                    if (outcome.CapabilitiesStale)
                        await provider.TryGetCapabilitiesAsync(refresh: true);

                    return (false, outcome.ErrorMessage ?? (claiming ? "Claim failed." : "Registration failed."));
                }

                // The account exists but nothing is signed in: registration mints no token. Follow
                // straight through with the credentials already in hand rather than asking the user
                // to retype what they just chose.
                //
                // Note this is also where WL-138's ownership guard runs, and it can legitimately
                // refuse: a brand new account signing in against a database that already belongs to
                // someone else is exactly the case the guard exists for. The account is still
                // created - that happened server-side - so the message has to be about the sign-in.
                var (signedIn, message) = await SignInAsync(email, password);
                var created = claiming ? "Claimed this server" : "Account created";

                return signedIn
                    ? (true, $"{created}. {message}")
                    : (false, $"{created}, but signing in failed. {message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Account creation failed: {ex.Message}");
                return (false, $"{(claiming ? "Claim" : "Registration")} failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- WL-139/140: account management
        //
        // These are account operations rather than sync operations, and they live here anyway.
        // This class already owns the HttpClient, the token store and the one ApiSyncProvider, and
        // it is "all MainWindowViewModel sees of the sync stack"; a second HTTP-owning service for
        // five calls would duplicate all three and give the view model two things to hold.
        //
        // Every one of them keeps the never-throws contract: an unreachable server, a
        // misconfiguration or a refusal comes back as a result, never as an exception.

        /// <summary>
        /// The devices signed in to this account. WL-141.
        /// </summary>
        /// <remarks>
        /// Order is the server's and is preserved all the way to the list on screen.
        /// </remarks>
        public async Task<DeviceListOutcome> GetDevicesAsync()
        {
            var provider = EnsureApiProvider();
            if (provider is null)
                return DeviceListOutcome.Failed("Enter an API endpoint first.");

            try
            {
                return await provider.GetDevicesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Device list failed: {ex.Message}");
                return DeviceListOutcome.Failed($"Couldn't load this account's devices: {ex.Message}");
            }
        }

        /// <summary>
        /// Detaches one device from the account. WL-141.
        /// </summary>
        /// <remarks>
        /// Removing a device revokes its token and nothing else - its projects, tasks and entries
        /// stay on the account, and it re-enrols by signing in again.
        /// <para>
        /// <b>Removing THIS device is allowed</b>, and the token this app is holding is dead the
        /// instant the server answers. The session is therefore stood down here rather than left
        /// to be discovered as a 401 on the next tick: the app would otherwise keep a token it
        /// already knows is worthless, and report a revoked session for something the user just
        /// asked for. Local data is untouched, exactly as an ordinary sign-out leaves it.
        /// </para>
        /// </remarks>
        public async Task<(bool Success, string Message)> RemoveDeviceAsync(string deviceId)
        {
            var provider = EnsureApiProvider();
            if (provider is null)
                return (false, "Enter an API endpoint first.");

            try
            {
                var outcome = await provider.DeleteDeviceAsync(deviceId);
                if (!outcome.Success)
                    return (false, outcome.ErrorMessage ?? "Couldn't remove that device.");

                var removedThisDevice = string.Equals(
                    deviceId, _settings.DeviceId, StringComparison.OrdinalIgnoreCase);

                if (!removedThisDevice)
                    return (true, "Device removed. Its recorded work stays on the account.");

                SignOut();
                return (true,
                    "This device was removed, so it's been signed out here. Your projects, tasks and "
                    + "entries stay on this machine and on the account - sign in again to reconnect it.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Device removal failed: {ex.Message}");
                return (false, $"Couldn't remove that device: {ex.Message}");
            }
        }

        /// <summary>
        /// Signs every device out, keeping this one signed in. WL-142.
        /// </summary>
        /// <remarks>
        /// Deliberately distinct from <see cref="SignOut"/>: that one drops this app's token and
        /// tells the server nothing, this one kills every token the account has and hands back a
        /// replacement for this device. Both leave local data completely alone.
        /// </remarks>
        public async Task<(bool Success, string Message)> SignOutEverywhereAsync()
        {
            var provider = EnsureApiProvider();
            if (provider is null)
                return (false, "Enter an API endpoint first.");

            try
            {
                var outcome = await provider.LogoutAllAsync();

                // The provider has already stored the fresh token, so this device is still signed
                // in - which is the whole point, and why nothing is cleared here.
                return outcome.Success
                    ? (true, "Every other device has been signed out. This one is still signed in, "
                             + "and no work was deleted anywhere.")
                    : (false, outcome.ErrorMessage ?? "Couldn't sign the other devices out.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Sign out everywhere failed: {ex.Message}");
                return (false, $"Couldn't sign the other devices out: {ex.Message}");
            }
        }

        /// <summary>
        /// Changes the account password. WL-142.
        /// </summary>
        /// <remarks>
        /// A wrong current password is reported as such and <b>does not</b> sign this app out - see
        /// <c>credentialGuarded</c> in <c>ApiSyncProvider</c> for how that 401 is told apart from a
        /// revoked session. On success every other device is signed out, because the server rotates
        /// the security stamp, and saying so is the difference between a feature and a mystery.
        /// </remarks>
        public async Task<(bool Success, string Message)> ChangePasswordAsync(
            string currentPassword, string newPassword)
        {
            var provider = EnsureApiProvider();
            if (provider is null)
                return (false, "Enter an API endpoint first.");

            if (string.IsNullOrEmpty(currentPassword))
                return (false, "Enter your current password.");

            if (string.IsNullOrEmpty(newPassword))
                return (false, "Enter a new password.");

            try
            {
                var outcome = await provider.ChangePasswordAsync(currentPassword, newPassword);

                return outcome.Success
                    ? (true, "Password changed. Your other devices have been signed out and will "
                             + "need the new password.")
                    : (false, outcome.ErrorMessage ?? "Couldn't change the password.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Password change failed: {ex.Message}");
                return (false, $"Couldn't change the password: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes the account on the server. WL-143. <b>Immediate, total and irreversible.</b>
        /// </summary>
        /// <remarks>
        /// What this does <i>not</i> do is the important half: <b>nothing local is deleted</b>. The
        /// SQLite database, every project, task and time entry in it, and every pending
        /// <c>SyncQueue</c> row are all left exactly as they were. That is the promise the
        /// confirmation dialog makes, and it is asserted by a test that counts rows.
        /// <para>
        /// The queue keeping its unsent rows is a decision, not an oversight. They can never reach
        /// the deleted account, but <c>PruneSyncedQueueAsync</c> only removes <c>IsSynced</c> ones
        /// and with sync switched off nothing tries to send them - so they cost nothing, and they
        /// are exactly what a later sign-in to a different account would want pushed.
        /// </para>
        /// <para>
        /// The email is mirrored against the signed-in address <b>before</b> anything is sent. The
        /// server checks it too and answers a mismatch with a 400, but only after it has checked
        /// the password - so letting a typo through would spend one of the five attempts standing
        /// between this user and a lockout, for a mistake this client can see.
        /// </para>
        /// <para>
        /// On any failure nothing local changes at all. The server guarantees a failed guard leaves
        /// the account fully intact, so pre-emptively clearing a session that still works would
        /// invent the very problem the guard prevented.
        /// </para>
        /// </remarks>
        public async Task<(bool Success, string Message)> DeleteAccountAsync(string password, string confirmEmail)
        {
            var provider = EnsureApiProvider();
            if (provider is null)
                return (false, "Enter an API endpoint first.");

            if (string.IsNullOrEmpty(password))
                return (false, "Enter your password to confirm.");

            // The local half of the typed-confirmation gate. Normalized the same way the ownership
            // stamp normalizes, so trailing whitespace and case differences are not a "mismatch".
            var typed = DatabaseOwner.NormalizeEmail(confirmEmail);
            var signedInAs = DatabaseOwner.NormalizeEmail(_settings.ApiEmail);

            if (string.IsNullOrEmpty(typed) || !string.Equals(typed, signedInAs, StringComparison.Ordinal))
            {
                return (false,
                    $"Type {_settings.ApiEmail} exactly to confirm. Nothing has been sent to the server.");
            }

            try
            {
                var outcome = await provider.DeleteAccountAsync(password, confirmEmail.Trim());
                if (!outcome.Success)
                    return (false, outcome.ErrorMessage ?? "Couldn't delete the account.");

                await StandDownAfterAccountDeletionAsync();

                return (true,
                    "The account and everything the server held for it have been deleted. Your "
                    + "projects, tasks and entries are still here on this device, and sync has been "
                    + "turned off. Use Export CSV on the Reports tab to keep a copy elsewhere.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Account deletion failed: {ex.Message}");
                return (false, $"Couldn't delete the account: {ex.Message}");
            }
        }

        // The client-side aftermath of a deletion the server has already carried out.
        //
        // Every one of these is about the SESSION. Switching the mode off is the load-bearing part:
        // the account is gone, so every request from here on is a permanent 401, and a 15-minute
        // timer grinding against it forever - producing a "you have been signed out" the user can
        // do nothing about - is the failure mode this exists to prevent. The endpoint is left in
        // settings on purpose, because it is still the right server to point a new account at.
        private async Task StandDownAfterAccountDeletionAsync()
        {
            DiscardSession();

            _settings.ApiEmail = string.Empty;
            _settings.SyncMode = SyncMode.Off;

            // Account-scoped state in a global field, the same reason adopting a database nulls it:
            // a cursor from the deleted account means nothing to whatever is signed into next.
            _settings.LastSyncedAtUtc = null;
            _settingsService.SaveSettings(_settings);

            // WL-138's stamp named an account that no longer exists, so it can no longer protect
            // anything - all it could still do is refuse this database to its own owner. Dropping
            // the identity (and nothing else) leaves it adoptable, which is where an unsynced
            // install starts anyway. See LocalDbService.ReleaseDatabaseOwnerAsync.
            await _db.ReleaseDatabaseOwnerAsync();

            // Stops the timer for real rather than at the next settings save.
            _builtForConfiguration = string.Empty;
            ApplyConfiguration();
        }

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
                var result = await _orchestrator!.RunSyncAsync();

                // WL-134: "sync failed: Sync server returned 401 Unauthorized" is technically
                // accurate and useless. Replace it with the one sentence that says what happened
                // and what to do, while keeping the counts - a run can have pushed nothing and
                // still be worth reporting honestly.
                return result.AuthenticationRequired ? WithAuthMessage(result) : result;
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
        /// Signs in to the configured WorkLedger-API instance, stores the JWT it issues, and
        /// checks that this local database actually belongs to the account that just signed in.
        /// The password is used for this call only and is never persisted. WL-134/WL-137/WL-138.
        /// </summary>
        /// <remarks>
        /// The ownership check necessarily happens AFTER the login rather than before it: the
        /// server's UserId is the thing being checked, and asking for it is what logging in is.
        /// On a refusal the token is therefore discarded again, so the observable outcome is
        /// still "nothing was kept". The one thing that cannot be undone is the device row the
        /// server enrolled as a side effect - harmless (a device row is provenance, not access to
        /// any data) and removable from account management later.
        /// </remarks>
        public async Task<(bool Success, string Message)> SignInAsync(string email, string password)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiEndpoint))
                return (false, "Enter an API endpoint first.");

            if (string.IsNullOrWhiteSpace(email))
                return (false, "Enter the email address you sign in with.");

            if (string.IsNullOrWhiteSpace(password))
                return (false, "Enter your password.");

            _credential = new ApiCredentials(email.Trim(), password);
            try
            {
                // WL-138 item 6d: a pre-S6 install being seen for the first time since the
                // cutover. Idempotent and a no-op for everyone else, so it costs one read.
                await _db.BackfillDatabaseOwnerAsync(_settings.LastSyncedAtUtc.HasValue);

                // The shared instance, not a throwaway - so a rejection recorded here is the same
                // verdict the sync path and the UI read afterwards. It exists whether or not the
                // mode is Api yet: signing in is what makes Api mode usable, so requiring the mode
                // to be selected and valid first would be the wrong way round.
                var provider = EnsureApiProvider();
                if (provider is null)
                    return (false, "Enter an API endpoint first.");

                var outcome = await provider.TryLoginAsync();
                if (!outcome.Success || outcome.User is not { } user)
                {
                    RecordSignInFailure(email);

                    // Surface the provider's own sentence: it is the only thing that knows whether
                    // this was a wrong password, a lockout, a rate limit or an unreachable server,
                    // and those want to be said differently.
                    //
                    // What it will NOT say is which of unknown-email, wrong-password and
                    // unclaimed-account this was. The server answers all three with an identical
                    // 401 to stop anonymous account enumeration, and the client is the last place
                    // that distinction could leak. Note registration deliberately does the opposite
                    // and 409s a duplicate email - that asymmetry is intentional, because getting
                    // that far required the signup secret. Do not "fix" either half to match the
                    // other.
                    return (false, outcome.ErrorMessage ?? "Sign-in failed.");
                }

                ResetSignInFailures();

                // Read before writing: the claim below overwrites the stamp, and the previous
                // endpoint is the thing an EndpointChanged message has to name.
                var previousOwner = await _db.GetDatabaseOwnerAsync();
                var verdict = await _db.EvaluateOwnershipAsync(user.Id, user.Email, _settings.ApiEndpoint);
                if (verdict == DatabaseOwnershipVerdict.DifferentAccount)
                {
                    // REFUSE - deliberately not "are you sure?". There is no confirmation wording
                    // that makes irreversible commingling of two people's work a reasonable thing
                    // to consent to in a modal, because the consequence is invisible at the moment
                    // of consent and permanent afterwards.
                    DiscardSession();
                    return (false, DescribeOwnershipRefusal(previousOwner, user.Email));
                }

                var adopting = verdict == DatabaseOwnershipVerdict.Unclaimed;
                await _db.ClaimDatabaseAsync(user.Id, user.Email, _settings.ApiEndpoint);

                _settings.ApiEmail = user.Email;

                // WL-138 item 6f: the pull cursor is account-scoped state living in a global
                // field. Carried across an ownership change it would start the new account's
                // first pull from an instant that means nothing to it, silently skipping
                // everything older. Nulling it costs one full pull, which the version merge makes
                // cheap, and skips nothing.
                if (adopting)
                    _settings.LastSyncedAtUtc = null;

                _settingsService.SaveSettings(_settings);

                // Force a rebuild: the orchestrator reads the cursor once, in its constructor, and
                // the provider it holds was built before there was a session.
                _builtForConfiguration = string.Empty;
                ApplyConfiguration();

                var signedIn = $"Signed in as {user.Email}.";

                // WL-138 item 6b: a moved server is warned about, not blocked. Saying which
                // address it moved from is what lets the user notice if it was the wrong one.
                return verdict == DatabaseOwnershipVerdict.EndpointChanged
                    ? (true, $"{signedIn} Note this data was last synced against {previousOwner?.ApiEndpoint}.")
                    : (true, signedIn);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Sign-in failed: {ex.Message}");
                return (false, $"Sign-in failed: {ex.Message}");
            }
            finally
            {
                // The line that makes "we do not store your password" true in the process as well
                // as on disk.
                _credential = null;
            }
        }

        /// <summary>
        /// Signs this device out. WL-138 item 6h: an auth event, never a data event.
        /// </summary>
        /// <remarks>
        /// Clears the token and nothing else. The email stays so the form prefills; the database,
        /// the pending queue, the pull cursor and the ownership stamp all survive, because the app
        /// has to remain fully usable with no server - that is the whole premise of the product,
        /// and signing out is not a request to stop working.
        /// </remarks>
        public void SignOut()
        {
            DiscardSession();

            // Rebuild so the next run uses a provider that knows it has no token, rather than one
            // still holding the state of the session that just ended.
            _builtForConfiguration = string.Empty;
            ApplyConfiguration();
        }

        // WL-146 item 2a. Counted per email, so switching accounts starts a fresh count - the
        // server's budget is per user too, and carrying one email's failures onto another would
        // warn about a threshold that address is nowhere near.
        private void RecordSignInFailure(string email)
        {
            var normalized = DatabaseOwner.NormalizeEmail(email);
            if (!string.Equals(_failureCountEmail, normalized, StringComparison.Ordinal))
            {
                _failureCountEmail = normalized;
                _consecutiveFailedSignIns = 0;
            }

            _consecutiveFailedSignIns++;
        }

        private void ResetSignInFailures()
        {
            _consecutiveFailedSignIns = 0;
            _failureCountEmail = string.Empty;
        }

        // Drops the session and only the session. Shared by SignOut and by the ownership refusal,
        // which has to leave the tree exactly as it found it.
        private void DiscardSession()
        {
            _settings.ApiToken = string.Empty;
            _settings.ApiTokenExpiresAtUtc = null;
            _settingsService.SaveSettings(_settings);
        }

        // WL-138 item 6g. Name both accounts, say what would have happened, and point at the
        // remedy that already exists - a separate data directory, which is the manual version of
        // the per-account profiles filed as WL-148.
        private static string DescribeOwnershipRefusal(DatabaseOwner? owner, string signingInAs)
            => $"This database belongs to {owner?.Email ?? "another account"}, not {signingInAs}. "
               + "Signing in would merge both accounts' work together permanently. "
               + "To use a different account on this machine, switch to a separate data directory "
               + "in Settings first.";

        public void Dispose()
        {
            _orchestrator?.StopSync();

            if (_ownsHttpClient)
                _http.Dispose();
        }

        // Null when the current settings can actually drive a sync, otherwise the reason they
        // can't - phrased for a toast, since that's where it ends up.
        private string? DescribeConfigurationProblem() => _settings.SyncMode switch
        {
            SyncMode.Off => "Sync is turned off.",
            SyncMode.File when string.IsNullOrWhiteSpace(_settings.SyncFolderPath) => "Choose a sync folder first.",
            SyncMode.Api when string.IsNullOrWhiteSpace(_settings.ApiEndpoint) => "Enter an API endpoint first.",

            // WL-137: no token at all. "Signed in but revoked" deliberately does NOT land here -
            // it is not a configuration problem, the run should still be attempted (a revocation
            // that was really a server restart heals itself), and it is reported afterwards by
            // WithAuthMessage.
            SyncMode.Api when string.IsNullOrWhiteSpace(_settings.ApiToken) => "Sign in to sync.",
            _ => null
        };

        // Keeps the run's counts and replaces only the sentence, so "0 sent, 0 received" stays
        // true while the error says something a person can act on.
        private SyncRunResult WithAuthMessage(SyncRunResult result) => new()
        {
            Ran = result.Ran,
            Succeeded = false,
            AuthenticationRequired = true,
            ErrorMessage = DescribeAuthState(),
            PushedCount = result.PushedCount,
            ReceivedCount = result.ReceivedCount
        };

        private void EnsureOrchestrator()
        {
            var configuration = string.Join(
                '|',
                _settings.SyncMode,
                _settings.SyncFolderPath,
                _settings.ApiEndpoint,
                // WL-138 item 6g: a changed identity must rebuild the provider rather than reuse
                // one still bound to the previous session.
                _settings.ApiEmail,
                _settings.SyncRetentionDays);

            if (_orchestrator != null && configuration == _builtForConfiguration)
                return;

            _orchestrator?.StopSync();

            var orchestrator = new SyncOrchestrator(_db, BuildProvider(), _syncStore, RunPreMergeBackupAsync);
            orchestrator.SyncCompleted += (_, e) => SyncCompleted?.Invoke(this, e);

            _orchestrator = orchestrator;
            _builtForConfiguration = configuration;
        }

        // The single Api provider, built on demand and rebuilt only when the endpoint changes.
        // Null when there is no endpoint to talk to, which is a configuration state rather than a
        // failure - every caller reads null as "nothing to ask yet".
        private ApiSyncProvider? EnsureApiProvider()
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiEndpoint))
                return null;

            if (_apiProvider is not null
                && string.Equals(_apiProviderEndpoint, _settings.ApiEndpoint, StringComparison.OrdinalIgnoreCase))
                return _apiProvider;

            _apiProviderEndpoint = _settings.ApiEndpoint;
            _apiProvider = new ApiSyncProvider(
                _http, _settings.ApiEndpoint, _settings.DeviceId, _syncStore, () => _credential, DeviceLabel());

            return _apiProvider;
        }

        private ISyncProvider BuildProvider() => _settings.SyncMode switch
        {
            SyncMode.File => new FileSyncProvider(
                _settings.SyncFolderPath,
                _settings.DeviceId,
                TimeSpan.FromDays(Math.Max(1, _settings.SyncRetentionDays))),

            // The same instance the auth panel uses, so one 401 is one verdict everywhere.
            SyncMode.Api => EnsureApiProvider() ?? (ISyncProvider)new NullSyncProvider(),

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
