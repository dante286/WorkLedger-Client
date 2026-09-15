using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
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

        // The Api-mode provider currently in use, kept so the auth state has somewhere to be read
        // from. Null in File and Off modes, which have no session to lose.
        private ApiSyncProvider? _apiProvider;

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

                // Built here rather than reused from the orchestrator so a user can sign in while
                // the mode is still being set up - signing in is what makes Api mode usable, so
                // requiring it to already be selected and valid would be the wrong way round.
                var provider = new ApiSyncProvider(
                    _http, _settings.ApiEndpoint, _settings.DeviceId, _syncStore, () => _credential, DeviceLabel());

                var outcome = await provider.TryLoginAsync();
                if (!outcome.Success || outcome.User is not { } user)
                {
                    // Surface the provider's own sentence: it is the only thing that knows whether
                    // this was a wrong password, a lockout, a rate limit or an unreachable server,
                    // and those want to be said differently.
                    _apiProvider = provider;
                    return (false, outcome.ErrorMessage ?? "Sign-in failed.");
                }

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

        private ISyncProvider BuildProvider()
        {
            // Dropped whenever the mode is not Api, so ApiAuth can never report the state of a
            // session belonging to a configuration that is no longer in use.
            if (_settings.SyncMode != SyncMode.Api)
                _apiProvider = null;

            return BuildProviderCore();
        }

        private ISyncProvider BuildProviderCore() => _settings.SyncMode switch
        {
            SyncMode.File => new FileSyncProvider(
                _settings.SyncFolderPath,
                _settings.DeviceId,
                TimeSpan.FromDays(Math.Max(1, _settings.SyncRetentionDays))),

            SyncMode.Api => _apiProvider = new ApiSyncProvider(
                _http, _settings.ApiEndpoint, _settings.DeviceId, _syncStore, () => _credential, DeviceLabel()),

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
