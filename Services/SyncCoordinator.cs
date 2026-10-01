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
    public class SyncCoordinator : IDisposable, IProfileAware
    {
        private const string PreMergeBackupFolderName = "pre-merge-backups";

        // Enough to survive a couple of bad sync cycles without turning the data directory
        // into an unbounded copy of every merge that ever happened.
        private const int PreMergeBackupsToKeep = 5;

        private readonly LocalDbService _db;
        private readonly SettingsService _settingsService;
        private readonly AppSettings _settings;
        private readonly SettingsSyncStore _syncStore;

        // WL-148. Null when this install has no profile machinery - which is every test that
        // constructs a coordinator directly, and is why the ownership REFUSAL below is still
        // reachable and still tested. The guard is the invariant; profiles are the convenience
        // built on top of it, and a coordinator without them behaves exactly as WL-138 left it.
        private readonly ProfileService? _profiles;

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
            HttpClient? httpClient = null,
            ProfileService? profiles = null)
        {
            _db = db;
            _settingsService = settingsService;
            _settings = settings;
            _syncStore = new SettingsSyncStore(settingsService, settings);
            _ownsHttpClient = httpClient is null;
            _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _profiles = profiles;
        }

        /// <summary>
        /// WL-148: stops the sync tick before the database underneath it is swapped.
        /// </summary>
        public Task OnProfileChangingAsync()
        {
            _orchestrator?.StopSync();
            return Task.CompletedTask;
        }

        /// <summary>
        /// WL-148: rebuilds everything that belonged to the account just left.
        /// </summary>
        /// <remarks>
        /// <b>Dropping the cached <c>ApiSyncProvider</c> is the load-bearing line.</b> It is keyed
        /// by endpoint, and the endpoint does not change when the profile does - so without this,
        /// the new account would inherit the previous one's 401 verdict, its capability cache and
        /// its rejected credential, and a revocation belonging to somebody else would be reported
        /// as this session's.
        /// </remarks>
        public Task OnProfileChangedAsync()
        {
            _apiProvider = null;
            _apiProviderEndpoint = string.Empty;
            _preMergeBackupTakenThisSession = false;
            ResetSignInFailures();

            _builtForConfiguration = string.Empty;
            ApplyConfiguration();

            return Task.CompletedTask;
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
        /// any data) and removable on the Devices page of the server's web portal.
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

            // WL-148, and it has to be captured BEFORE the login. ApiSyncProvider writes the token
            // it is issued straight into the live AppSettings through SettingsSyncStore - settings
            // that, until any switch happens, still belong to the profile being signed in FROM. So
            // the outgoing profile's own session is remembered here and handed back to it below.
            var sessionBefore = new ProfileSession(
                _settings.ApiEmail, _settings.ApiToken, _settings.ApiTokenExpiresAtUtc, _settings.LastSyncedAtUtc);
            var profileBefore = _profiles?.ActiveProfile;

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
                    // that distinction could leak.
                    return (false, outcome.ErrorMessage ?? "Sign-in failed.");
                }

                ResetSignInFailures();

                // Read before writing: the claim below overwrites the stamp, and the previous
                // endpoint is the thing an EndpointChanged message has to name.
                var previousOwner = await _db.GetDatabaseOwnerAsync();
                var verdict = await _db.EvaluateOwnershipAsync(user.Id, user.Email, _settings.ApiEndpoint);

                // WL-148: the issued token, before anything below can move the live settings to a
                // different profile.
                var issued = new ProfileSession(
                    user.Email, _settings.ApiToken, _settings.ApiTokenExpiresAtUtc, _settings.LastSyncedAtUtc);

                var switchedTo = await SwitchProfileForSignInAsync(verdict, user, profileBefore, sessionBefore);
                if (switchedTo.Refused)
                    return (false, DescribeOwnershipRefusal(previousOwner, user.Email));

                if (switchedTo.Profile is not null)
                {
                    // The live settings now belong to the profile we moved to, so the token this
                    // sign-in earned has to be written into it. Its previous occupant's token went
                    // back into its own profile.json inside the switch.
                    _settings.ApiToken = issued.Token;
                    _settings.ApiTokenExpiresAtUtc = issued.ExpiresAtUtc;

                    // And start the provider over. Between the switch and the line above, this
                    // endpoint's provider was briefly holding the profile we moved INTO with its
                    // own older token; anything that asked it something in that window could have
                    // recorded a 401 verdict, and a verdict outranks the token's own shape - so a
                    // perfectly good session would report itself revoked.
                    _apiProvider = null;
                    _apiProviderEndpoint = string.Empty;

                    // Re-read against the database we actually landed on. A profile's registry
                    // entry is advisory and its stamp is authoritative, so the only honest way to
                    // know what we are now looking at is to ask it again.
                    previousOwner = await _db.GetDatabaseOwnerAsync();
                    verdict = await _db.EvaluateOwnershipAsync(user.Id, user.Email, _settings.ApiEndpoint);

                    if (verdict == DatabaseOwnershipVerdict.DifferentAccount)
                    {
                        DiscardSession();
                        return (false, DescribeOwnershipRefusal(previousOwner, user.Email));
                    }
                }

                var adopting = verdict == DatabaseOwnershipVerdict.Unclaimed;
                await _db.ClaimDatabaseAsync(user.Id, user.Email, _settings.ApiEndpoint);

                // WL-148: the registry's mirror of the stamp that was just written. Safe to call
                // for a profile that already names this account - it re-records the same identity
                // and touches LastUsedAtUtc.
                _profiles?.AdoptActiveProfile(user.Id, user.Email, _settings.ApiEndpoint);

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

                // WL-148: a switch is the one outcome the user has to be told about, because the
                // projects list on screen changed underneath them and the reason is not obvious.
                if (switchedTo.Profile is not null)
                {
                    return (true, switchedTo.Created
                        ? $"{signedIn} Started a fresh local database for this account - your other "
                          + "account's work is untouched and still on this machine."
                        : $"{signedIn} Switched to this account's own local data.");
                }

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

        // The account-scoped half of AppSettings, carried across a profile switch. WL-148.
        private readonly record struct ProfileSession(
            string Email, string Token, DateTime? ExpiresAtUtc, DateTime? LastSyncedAtUtc);

        // What the profile decision below concluded. Three outcomes, because they want three
        // different things said: nothing at all, "you are now looking at another database", and
        // WL-138's refusal.
        private readonly record struct SignInProfileOutcome(Profile? Profile, bool Created, bool Refused)
        {
            public static SignInProfileOutcome StayedPut => new(null, false, false);

            public static SignInProfileOutcome Refuse() => new(null, false, true);
        }

        /// <summary>
        /// WL-148: gives the account that just signed in its own local database, switching to it if
        /// that isn't the one currently open.
        /// </summary>
        /// <remarks>
        /// Four cases, and the order between the middle two is the whole point:
        /// <list type="number">
        /// <item>the open database is already stamped for this account - stay;</item>
        /// <item>a profile is <b>already registered</b> for this account - switch to it. Ahead of
        /// adoption deliberately, because an account that ended up with two profiles would have its
        /// work split across two databases with no way to tell which half is which;</item>
        /// <item>the open database is unclaimed and its profile belongs to nobody - <b>adopt it in
        /// place</b>, with no prompt whatsoever. This is WL-138's offline-first path and it must
        /// stay frictionless: a week of work with no server, then a sign-in, and everything is
        /// simply kept;</item>
        /// <item>otherwise - create a profile for this account and switch to it.</item>
        /// </list>
        /// <para>
        /// If the switch cannot be made, the sign-in is <b>refused</b>, exactly as it was before
        /// profiles existed. The guard is the invariant and this is the convenience built on top of
        /// it; a convenience that fails must not be able to cost anyone their data.
        /// </para>
        /// </remarks>
        private async Task<SignInProfileOutcome> SwitchProfileForSignInAsync(
            DatabaseOwnershipVerdict verdict, UserSummary user, Profile? profileBefore, ProfileSession sessionBefore)
        {
            if (verdict is DatabaseOwnershipVerdict.SameAccount or DatabaseOwnershipVerdict.EndpointChanged)
                return SignInProfileOutcome.StayedPut;

            if (_profiles is null || profileBefore is null)
            {
                if (verdict != DatabaseOwnershipVerdict.DifferentAccount)
                    return SignInProfileOutcome.StayedPut;

                // WL-138 verbatim, and deliberately not "are you sure?". There is no confirmation
                // wording that makes irreversible commingling of two people's work a reasonable
                // thing to consent to in a modal, because the consequence is invisible at the
                // moment of consent and permanent afterwards.
                DiscardSession();
                return SignInProfileOutcome.Refuse();
            }

            var existing = _profiles.FindForAccount(user.Id, user.Email);
            var sameProfile = existing is not null
                && string.Equals(existing.Id, profileBefore.Id, StringComparison.OrdinalIgnoreCase);

            Profile target;
            bool created;

            if (existing is not null && !sameProfile)
            {
                target = existing;
                created = false;
            }
            else if (verdict == DatabaseOwnershipVerdict.Unclaimed && !profileBefore.HasIdentity)
            {
                return SignInProfileOutcome.StayedPut;
            }
            else
            {
                created = existing is null;
                target = _profiles.EnsureProfileForAccount(user.Id, user.Email, _settings.ApiEndpoint);
            }

            // Put the outgoing profile's own session back into the live settings BEFORE switching.
            // The switch persists them into that profile's profile.json on its way out, so this is
            // what stops the token this sign-in just earned from being filed under the account
            // being left behind - and it means a switch back needs no password.
            RestoreSession(sessionBefore);

            var switched = await _profiles.SwitchToAsync(target.Id);
            if (switched.Success)
                return new SignInProfileOutcome(target, created, false);

            // Nothing to undo: the live settings are already the outgoing profile's, and the
            // database was never closed.
            return SignInProfileOutcome.Refuse();
        }

        private void RestoreSession(ProfileSession session)
        {
            _settings.ApiEmail = session.Email;
            _settings.ApiToken = session.Token;
            _settings.ApiTokenExpiresAtUtc = session.ExpiresAtUtc;
            _settings.LastSyncedAtUtc = session.LastSyncedAtUtc;
            _settingsService.SaveSettings(_settings);
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

        /// <summary>
        /// "I've reset my password": forgets this server's lockout verdict so Sign in is usable
        /// again. WL-176, plan 12 item 6. <b>Sends nothing.</b>
        /// </summary>
        /// <remarks>
        /// The server clears its failed-login counters when a reset is redeemed, but this install
        /// cannot see that happen - the reset is done in a browser - and the provider holds its
        /// <c>LockedOut</c> verdict until the <c>Retry-After</c> lapses. Since
        /// <see cref="EnsureApiProvider"/> caches that instance by endpoint, Sign in would stay
        /// disabled for up to <c>Auth:LockoutMinutes</c> against a server that would accept the new
        /// password now. The user is the only one who knows better than the countdown, so this is
        /// theirs to press.
        /// <para>
        /// It drops the cached provider exactly as <see cref="OnProfileChangedAsync"/> does, and
        /// borrows nothing else from the profile machinery: the account and its database are the
        /// same, only the credential changed. It also discards any token this install still holds,
        /// because a reset rotates the account's security stamp and every existing token died with
        /// it. Keeping a dead one would make the panel read it as "signed in", and the next refresh
        /// would spend a request finding out otherwise.
        /// </para>
        /// <para>
        /// <b>Invariant L is untouched.</b> It bounds unattended attempts, and with no token the
        /// timer has nothing to run against - the next request is the user's own sign-in with the
        /// password they just chose. If the account is in fact still locked, that is answered with
        /// 423 again and the verdict comes straight back: one user-initiated attempt per press.
        /// </para>
        /// Returns false, and changes nothing, unless a lockout is actually showing.
        /// </remarks>
        public bool ForgetServerVerdict()
        {
            if (ApiAuth.State != AuthState.LockedOut)
                return false;

            _apiProvider = null;
            _apiProviderEndpoint = string.Empty;
            ResetSignInFailures();

            SignOut();
            return true;
        }

        /// <summary>
        /// "Detach from account": forgets which server account this profile's data belonged to,
        /// keeping every row of it. WL-176, plan 12 item 7.
        /// </summary>
        /// <remarks>
        /// The user-invoked replacement for WL-143's automatic release. When an account is deleted
        /// (or abandoned) in the web portal this install never hears about it, and the
        /// <c>DatabaseOwner</c> stamp keeps naming an account that no longer exists - so the next
        /// sign-in, even the same person re-registering with the same email and getting a new
        /// <c>UserId</c>, is routed to a brand new profile and the old work is stranded.
        /// <para>
        /// <b>Both halves are required.</b> <see cref="SwitchProfileForSignInAsync"/> adopts in place
        /// only when the database is unclaimed <i>and</i> its profile belongs to nobody; release the
        /// stamp alone and the registry entry still names the old account, and a new profile is
        /// created anyway. The registry half is a named method rather than a change to
        /// <c>ProfileService.ReconcileActiveProfileAsync</c>, which deliberately leaves an entry
        /// alone when the stamp has no identity - widening that would change what restoring an
        /// unstamped backup does to an account's profile.
        /// </para>
        /// <para>
        /// <b>Signed out only, active profile only.</b> Signed in, it would contradict the session in
        /// hand; another profile would need a database switch, and the user can switch first. The
        /// account-scoped settings are cleared with it - the email, the token and the pull cursor all
        /// belonged to the account being forgotten, exactly the fields WL-143 cleared.
        /// </para>
        /// <para>
        /// <b>Not one project, task, entry or <c>SyncQueue</c> row is touched.</b> The unsent queue
        /// is exactly what the next account will want pushed.
        /// </para>
        /// </remarks>
        public async Task<(bool Success, string Message)> DetachFromAccountAsync()
        {
            if (ApiAuth.State == AuthState.SignedIn)
                return (false, "Sign out first - this data is still attached to the account you're signed in with.");

            try
            {
                var owner = await _db.GetDatabaseOwnerAsync();
                var stamped = owner?.HasIdentity ?? false;
                var registered = _profiles?.ActiveProfile.HasIdentity ?? false;

                if (!stamped && !registered)
                    return (false, "This data isn't attached to an account, so there's nothing to detach.");

                // The authoritative half first. If anything after it fails, the database already
                // reads as Unclaimed, and ReconcileActiveProfileAsync never re-stamps a registry entry
                // from a stamp with no identity - so a half-done detach errs towards adoptable.
                await _db.ReleaseDatabaseOwnerAsync();
                _profiles?.ReleaseActiveProfile();

                // Whatever this endpoint's provider last concluded - a revocation, most likely, if
                // the account was deleted - was about the account that is being forgotten.
                _apiProvider = null;
                _apiProviderEndpoint = string.Empty;
                ResetSignInFailures();

                _settings.ApiEmail = string.Empty;
                _settings.ApiToken = string.Empty;
                _settings.ApiTokenExpiresAtUtc = null;

                // Account-scoped state in a profile field, nulled for the reason adoption nulls it:
                // a cursor from the old account means nothing to whichever account comes next.
                _settings.LastSyncedAtUtc = null;
                _settingsService.SaveSettings(_settings);

                _builtForConfiguration = string.Empty;
                ApplyConfiguration();

                return (true,
                    "Detached. Your projects, tasks and entries are all still here; the next account "
                    + "you sign in with will take them over and sync them to its server.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Detach from account failed: {ex.Message}");
                return (false, $"Couldn't detach this data from its account: {ex.Message}");
            }
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
            // WL-148: under the ACTIVE PROFILE's directory rather than the data directory, so one
            // account's five snapshots cannot evict another's - PruneOldPreMergeBackups keeps the
            // newest five of whatever it finds, and a shared root would let a busy profile quietly
            // discard the only pre-merge copy another one ever took.
            var root = Path.Combine(_settingsService.ProfileDirectory, PreMergeBackupFolderName);
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
