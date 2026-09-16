using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WorkLedger.Contracts.Auth;
using WorkLedger.Contracts.Sync;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;

namespace WorkLedger_Client.Sync
{
    // Sync against a WorkLedger-API instance. Plan 6.
    //
    // The wire types come from WorkLedger.Contracts (vendored under Sync/Contracts) rather
    // than being redefined here - that shared compilation is the entire reason the server
    // is C#. The rules this implements are the server's, not choices made here; see the
    // "Server contract (normative)" section of plans/06-api-sync-provider.md, which mirrors
    // WorkLedger-API/plans/S3-sync-endpoints.md. Two of them decide the shape of this class:
    //
    //   * Push results are POSITIONAL. SyncPushResponse.Results holds exactly one entry per
    //     input change, in request order. SyncItemResult.EntityId is not a usable key - one
    //     batch legitimately carries several queue rows for the same entity (create ->
    //     update -> delete), so a Guid maps to N rows, not one. See WL-94.
    //   * Accepted = false is FINAL. The server rejects either for a validation failure or
    //     because its own version won, and neither changes on a re-push. Those rows are
    //     retired, not retried.
    //
    // Two server behaviours look like bugs from here and are not:
    //   * An accepted delete comes back with a HIGHER AuthoritativeVersion than was pushed.
    //     The server stores tombstones at existing.Version + 1 so they beat every peer's
    //     copy - an accommodation for WL-93. Not a rejection.
    //   * An accepted change may not have been written at all. A same-or-lower version from
    //     the device that authored the server's row is a replay/self-echo/lost ack, and the
    //     server acks it without writing. "Accepted" never means "the server's row now
    //     equals what I sent".
    //
    // WL-134 (plan 8). Authentication is POST /auth/login, not the deleted
    // POST /auth/register-device, and that changes what a 401 MEANS here. The server now
    // validates a security stamp on every request, so a token dies for reasons that have
    // nothing to do with its expiry: a password change, a sign-out-everywhere, this device
    // being deleted from another machine, or the account being deleted. A 401 is therefore a
    // statement about the SESSION, not about the token's age.
    //
    // That rules out the obvious translation of the old behaviour. "On a 401, quietly get
    // another token" was safe when getting one needed only a secret the app held; getting one
    // now needs a password, which this client deliberately never stores, and the server locks
    // an account for 15 minutes after 5 consecutive failed logins - refusing the CORRECT
    // password for the duration. A background sync that answered every 401 with a login
    // attempt would lock its own user out of their own server overnight, unattended.
    //
    // The whole design rests on one observation:
    //
    //   A 401 from POST /auth/login is DETERMINISTIC. The credential was wrong, and the same
    //   credential will be wrong next time. Retrying cannot succeed - it can only spend one of
    //   the five attempts standing between the user and a lockout.
    //
    // So the correct automated retry count for a rejected credential is zero, not "a few, with
    // backoff" - backoff is an instrument for failures that might resolve themselves, and a
    // wrong password is not one. Hence:
    //
    //   INVARIANT L: over the entire lifetime of the app, at most ONE unattended failed
    //   /auth/login attempt is made per distinct credential presented, after which that
    //   credential is discarded. Since the server's threshold is five CONSECUTIVE failures, no
    //   amount of running time can reach it.
    //
    // _rejectedCredential is where that is enforced; CurrentCredential is where it is read.
    // Note it constrains the mechanism rather than the feature, so a future "remember me" is
    // safe to add: a remembered credential still gets one attempt and is then discarded.
    //
    // One subtlety that decides several details: the server's FailedLoginCount lives on the
    // USER row, not the device row, so failures from a phone and a laptop share one budget.
    // This client can never know the true remaining count, and must never automate on the
    // assumption that it has attempts to spare.
    public class ApiSyncProvider : IAuthenticatingSyncProvider
    {
        // WL-146: the ONE place the bootstrap secret survives plan 8. It is no longer a
        // token-issuing credential - on the server it gates only POST /auth/register and
        // POST /auth/claim, i.e. who may create an account - so it belongs to a registration form
        // for the duration of one call and nowhere near the sync configuration.
        private const string BootstrapSecretHeader = "X-Bootstrap-Secret";

        private readonly HttpClient _http;
        private readonly Uri _baseUri;
        private readonly string _deviceId;
        private readonly IApiTokenStore _tokens;
        private readonly Func<ApiCredentials?> _credentialsAccessor;
        private readonly string? _deviceLabel;

        // The server's last word on this session, when it has said one. Null means "nothing has
        // been refused", and the state is then derived from the token itself - which is free and
        // needs no request.
        //
        // Deliberately NOT persisted. Re-checking a revocation after a restart costs one
        // authenticated request and zero login attempts, and a "revocation" that was really a
        // server restart heals itself instead of leaving a banner the user has to dismiss.
        private AuthStatus? _verdict;

        // WL-146: what GET /auth/capabilities said, cached for the life of this provider - which is
        // per-endpoint, since SyncCoordinator rebuilds the provider when the endpoint changes. The
        // answer is server configuration and does not move under a running server, so re-asking on
        // every panel open would be a request per glance.
        //
        // _capabilitiesProbed is deliberately NOT set after a transport failure: an unreachable
        // server has not answered, and caching "no capabilities" for the app run would leave the
        // registration UI hidden until restart for what may have been a five-second outage.
        private AuthCapabilitiesResponse? _capabilities;
        private bool _capabilitiesProbed;

        // INVARIANT L, enforced. The credential a login has already been refused for.
        //
        // Keeping it here rather than trusting the caller to stop offering one is the point: the
        // accessor is consulted on every 401, and without this a caller still holding a wrong
        // password - a settings screen left open, a future "remember me" - would spend one of the
        // user's five attempts per sync tick. Record equality compares by value, so a retyped
        // password is a DIFFERENT credential and gets its own single attempt; that is what makes
        // this a bound on failures rather than a ban on signing in.
        private ApiCredentials? _rejectedCredential;

        // HttpClient is injected, never constructed here: one socket-exhausting instance per
        // provider is the classic way to break an app that syncs on a timer.
        //
        // Credentials arrive through an accessor rather than a stored string, the same shape the
        // bootstrap secret used before them and for a sharper reason: this provider outlives an
        // interactive sign-in, the password must not, and an accessor lets the caller simply stop
        // answering. A null return is the normal state - it means "nobody is signing in right
        // now", which is true for all but a second or two of the app's life.
        public ApiSyncProvider(
            HttpClient httpClient,
            string baseUrl,
            string deviceId,
            IApiTokenStore tokenStore,
            Func<ApiCredentials?>? credentialsAccessor = null,
            string? deviceLabel = null)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("An API endpoint is required.", nameof(baseUrl));

            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            _deviceId = deviceId;
            _tokens = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
            _credentialsAccessor = credentialsAccessor ?? (() => null);
            _deviceLabel = deviceLabel;
        }

        /// <summary>
        /// This session as far as this provider knows - derived, not stored. Plan 8 item 4a.
        /// </summary>
        public AuthStatus Auth
        {
            get
            {
                // LockedOut and RateLimited are the only states that stop being true without
                // anyone doing anything, so they clear themselves off their own Retry-After.
                if (_verdict is { } expiring && expiring.HasLifted)
                    _verdict = null;

                // A server verdict outranks the token's own shape: a token that looks perfectly
                // good is still dead once the server has said so, and nothing about it locally
                // would ever reveal that.
                if (_verdict is { } verdict)
                    return verdict;

                if (string.IsNullOrWhiteSpace(_tokens.Token))
                    return AuthStatus.SignedOut;

                // The free one: expiry is knowable without asking, so a year-idle install
                // prompts for a sign-in rather than spending a doomed round trip finding out.
                return _tokens.ExpiresAtUtc is { } expiry && expiry <= DateTime.UtcNow
                    ? AuthStatus.SessionExpired
                    : AuthStatus.SignedIn;
            }
        }

        public async Task<SyncResult> PushAsync(IEnumerable<SyncQueue> pendingChanges)
        {
            // Order is the join key, so it's fixed here and never re-sorted. The server
            // re-orders internally to apply the batch but answers in request order.
            var changes = pendingChanges.ToList();
            if (changes.Count == 0)
                return new SyncResult();

            try
            {
                var request = new SyncPushRequest(changes.Select(ToChangeDto).ToList());
                var response = await SendForJsonAsync<SyncPushResponse>(
                    () => BuildJsonRequest(HttpMethod.Post, "sync/push", request));
                return MapPushResults(changes, response);
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider push failed: {ex.Message}");

                // Transport-level failure: nothing was decided, so every row stays pending
                // and is retried on the next tick. Note these go to Failures only - putting
                // them in RejectedSyncQueueIds would discard changes the server never saw.
                //
                // WL-134: an auth failure is the same kind of outcome. The rows stay pending
                // precisely because they were never delivered - retiring work on the strength of
                // a signed-out session would lose it for good.
                return new SyncResult
                {
                    AuthenticationRequired = !Auth.CanSync,
                    Failures = changes
                        .Select(change => new SyncFailure { SyncQueueId = change.Id, ErrorMessage = ex.Message })
                        .ToList()
                };
            }
        }

        public async Task<PullResult> PullAsync(DateTime since)
        {
            try
            {
                // "o" round-trips unambiguously. The server normalizes whatever it gets to
                // UTC (its columns are timestamptz) and treats DateTime.MinValue as a
                // legitimate request for everything.
                var path = $"sync/pull?since={Uri.EscapeDataString(since.ToString("o"))}";
                var response = await SendForJsonAsync<SyncPullResponse>(
                    () => new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, path)));

                return new PullResult
                {
                    Changes = response.Changes.Select(ToRemoteChange).ToList(),
                    ServerTime = response.ServerTime
                };
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider pull failed: {ex.Message}");
                return PullResult.Failed(ex.Message, authenticationRequired: !Auth.CanSync);
            }
        }

        // Cheap liveness/session check - useful to a settings screen wanting to say "connected"
        // without pushing anything, and the one authenticated request that can confirm a
        // revocation without costing a login attempt. Kept here rather than in the UI so it
        // shares the 401 policy with the real calls.
        //
        // Since S6 the response also carries the signed-in email and display name, so "who am I"
        // is answerable from the one call the client already makes.
        public async Task<WhoAmIResponse?> TryWhoAmIAsync()
        {
            try
            {
                return await SendForJsonAsync<WhoAmIResponse>(
                    () => new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "auth/whoami")));
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider whoami failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Signs in with whatever credential the accessor is currently offering and stores the
        /// JWT that comes back. Enrolling this device is a side effect server-side, idempotently,
        /// with the same semantics the deleted register-device call had.
        /// </summary>
        /// <remarks>
        /// The device identity sent is <c>AppSettings.DeviceId</c> - the same value stamped as
        /// <c>OriginDeviceId</c> on every row this install writes. There is deliberately exactly
        /// one of these, and the email is emphatically not it: the server keys device rows by
        /// <c>(UserId, DeviceId)</c>, so identity and enrolment are already separate there.
        /// <para>
        /// Never throws. A missing or already-rejected credential is a state, not a failure, and
        /// so is an unreachable server.
        /// </para>
        /// </remarks>
        public async Task<LoginOutcome> TryLoginAsync()
        {
            var credential = CurrentCredential();
            if (credential is null)
                return new LoginOutcome(false, null, "No sign-in details are available.");

            try
            {
                // Deliberately SendOnce, not SendFor: this IS the authentication path, so there
                // is nothing for a 401 here to recover with, and routing it through the retry
                // wrapper would let one rejected password become two attempts.
                var response = await SendOnceForJsonAsync<LoginResponse>(() => BuildJsonRequest(
                    HttpMethod.Post,
                    "auth/login",
                    new LoginRequest(credential.Email, credential.Password, _deviceId, _deviceLabel)));

                _tokens.Save(response.Token, response.ExpiresAtUtc);

                // A fresh token settles every question the old state was an answer to, including
                // an earlier rejection of this same credential - the password may simply have
                // been changed back, or fixed elsewhere.
                _rejectedCredential = null;
                _verdict = null;

                return new LoginOutcome(true, response.User, null);
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider login failed: {ex.Message}");
                return new LoginOutcome(false, null, RecordLoginFailure(ex, credential));
            }
        }

        // Turns one failed login into an auth state and a sentence, and decides whether the
        // credential survives it. The three cases are genuinely different and the differences
        // are the entire point of plan 8 item 4c.
        private string RecordLoginFailure(SyncProviderException ex, ApiCredentials credential)
        {
            switch (ex.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    // *** INVARIANT L LIVES ON THIS LINE. *** The credential was refused, so it
                    // is refused permanently - the same email and password cannot become correct
                    // by being sent again, and every resend spends one of five attempts standing
                    // between this user and a lockout that would refuse their right password too.
                    // Discarding it here is what bounds the client to one unattended failure per
                    // credential no matter how long it runs. Do not "helpfully" retry past this.
                    _rejectedCredential = credential;
                    _verdict = AuthStatus.SignedOut;

                    // The server answers unknown-email and wrong-password with the same 401 on
                    // purpose, so that an attacker cannot enumerate accounts. Guessing which one
                    // it was for the user's benefit would undo that, so the message doesn't try.
                    return "Those sign-in details weren't accepted.";

                case HttpStatusCode.Locked:
                    // Note the credential is NOT discarded: a locked account refuses the correct
                    // password too, so a 423 says nothing about whether this one is right.
                    _verdict = AuthStatus.LockedOut(ex.RetryAfterUtc);
                    return DescribeWait("Too many failed sign-in attempts, so this account is temporarily locked", ex.RetryAfterUtc);

                case HttpStatusCode.TooManyRequests:
                    // Also not a verdict on the credential - this is a per-IP limit, and nobody's
                    // account is in trouble.
                    _verdict = AuthStatus.RateLimited(ex.RetryAfterUtc);
                    return DescribeWait("The server is rate-limiting sign-in attempts", ex.RetryAfterUtc);

                default:
                    // An unreachable server or a 500 says nothing about the credential either,
                    // and it may well work in a minute. Leave the state alone.
                    return ex.Message;
            }
        }

        private static string DescribeWait(string what, DateTime? until)
        {
            if (until is not { } instant)
                return what + ". Try again shortly.";

            var minutes = Math.Max(1, (int)Math.Ceiling((instant - DateTime.UtcNow).TotalMinutes));
            return $"{what}. Try again in about {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
        }

        // The one place a credential is read, and the one place Invariant L is enforced on the
        // way in. Null - "there is nothing to try" - for every reason that matters:
        //
        //   * nobody is signing in, which is the normal state;
        //   * the credential is incomplete, so sending it would waste an attempt on a typo we
        //     can see from here;
        //   * it has already been refused once (Invariant L);
        //   * the session is locked out or rate-limited, where the server has explicitly asked
        //     to be left alone and another attempt could only make the wait longer.
        private ApiCredentials? CurrentCredential()
        {
            var credential = _credentialsAccessor();
            if (credential is null
                || string.IsNullOrWhiteSpace(credential.Email)
                || string.IsNullOrWhiteSpace(credential.Password))
                return null;

            if (credential == _rejectedCredential)
                return null;

            return Auth.State is AuthState.LockedOut or AuthState.RateLimited ? null : credential;
        }

        /// <summary>
        /// What this server allows, so the UI can render the truth instead of guessing. WL-146.
        /// </summary>
        /// <remarks>
        /// Sent <b>anonymously</b>. The endpoint takes no bearer and a stale token must not be able
        /// to turn feature discovery into a 401 - being signed out is precisely when a client most
        /// needs to know whether it may offer to create an account.
        /// <para>
        /// Total, and null-on-anything-unhelpful by design. A <c>404</c> means a server predating
        /// the user-accounts work, which is a supported thing to point at rather than an error: the
        /// caller degrades to a sign-in-only UI. That is the same forward-compatibility stance the
        /// sync side takes for a missing <c>AuthoritativeOperation</c>.
        /// </para>
        /// </remarks>
        public async Task<AuthCapabilitiesResponse?> TryGetCapabilitiesAsync(bool refresh = false)
        {
            if (_capabilitiesProbed && !refresh)
                return _capabilities;

            try
            {
                _capabilities = await SendOnceForJsonAsync<AuthCapabilitiesResponse>(
                    () => new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "auth/capabilities")));
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider capabilities probe failed: {ex.Message}");
                _capabilities = null;

                // No status code means nothing answered - a dead host, DNS, a timeout. That is not
                // a statement about the server's features, so don't remember it as one.
                if (ex.StatusCode is null)
                    return null;
            }

            _capabilitiesProbed = true;
            return _capabilities;
        }

        /// <summary>
        /// Creates an account. WL-146. Returns no token - see <see cref="AccountOutcome"/>.
        /// </summary>
        public Task<AccountOutcome> TryRegisterAsync(
            string email, string password, string? displayName, string? bootstrapSecret)
            => CreateAccountAsync("auth/register", email, password, displayName, bootstrapSecret, claiming: false);

        /// <summary>
        /// Takes ownership of a server's pre-existing, unclaimed account. WL-146.
        /// </summary>
        /// <remarks>
        /// A deployment that predates user accounts migrates in place: its one seeded tenant keeps
        /// every project, task, entry and device and gains a null password hash marking it
        /// unclaimed. This attaches an email and password to <i>that row</i>, so the data already on
        /// the server carries over. It cannot be signed into first - login refuses a null hash - and
        /// once claimed the endpoint answers 409 forever, so this is a one-shot.
        /// <para>
        /// Deliberately <b>not</b> gated on <c>RegistrationEnabled</c>, on the server or here: an
        /// operator who closed signups must still be able to claim their own data.
        /// </para>
        /// </remarks>
        public Task<AccountOutcome> TryClaimAsync(
            string email, string password, string? displayName, string? bootstrapSecret)
            => CreateAccountAsync("auth/claim", email, password, displayName, bootstrapSecret, claiming: true);

        // Register and claim take the same three inputs and the same secret header; only the path
        // and two of the failure messages differ.
        private async Task<AccountOutcome> CreateAccountAsync(
            string path, string email, string password, string? displayName, string? bootstrapSecret, bool claiming)
        {
            try
            {
                var user = await SendOnceForJsonAsync<UserSummary>(() =>
                {
                    var request = BuildJsonRequest(
                        HttpMethod.Post, path, new RegisterUserRequest(email, password, displayName));

                    if (!string.IsNullOrWhiteSpace(bootstrapSecret))
                        request.Headers.TryAddWithoutValidation(BootstrapSecretHeader, bootstrapSecret);

                    return request;
                });

                return new AccountOutcome(true, user, null);
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider {path} failed: {ex.Message}");

                // A 403 (registration switched off) or a 409 (email taken / nothing left to claim)
                // both mean the cached capabilities are describing a server that has moved on since
                // the probe, so the caller re-asks rather than leaving a form on screen that can
                // only fail the same way again.
                var stale = ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict;
                return new AccountOutcome(false, null, DescribeAccountFailure(ex, claiming), stale);
            }
        }

        private static string DescribeAccountFailure(SyncProviderException ex, bool claiming) => ex.StatusCode switch
        {
            HttpStatusCode.Forbidden => "This server has account registration turned off.",

            HttpStatusCode.Unauthorized => "That signup secret wasn't accepted.",

            // The one case where the server's own words are better than ours: it knows why the
            // password or email was refused, and this client deliberately does not duplicate the
            // rule it would be restating.
            HttpStatusCode.BadRequest => ExtractServerError(ex) ?? "The server rejected those details.",

            HttpStatusCode.Conflict => claiming
                ? "There is nothing left to claim - this server's data already has an owner."
                : "An account with that email already exists.",

            // Worded about the ADDRESS, not about registration: the limiter is per-IP and shared
            // with /auth/login, so this may well have been earned by sign-in attempts - possibly
            // from a different device behind the same address.
            HttpStatusCode.TooManyRequests =>
                "Too many sign-in or signup attempts from this address. Try again shortly.",

            _ => ex.Message
        };

        // The server's error shape is {"error":"..."} (see its AuthEndpoints.Error). Best-effort:
        // anything unexpected falls back to the caller's generic wording rather than showing the
        // raw body.
        private static string? ExtractServerError(SyncProviderException ex)
        {
            if (string.IsNullOrWhiteSpace(ex.ResponseBody))
                return null;

            try
            {
                using var document = JsonDocument.Parse(ex.ResponseBody);
                return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ WL-140: account management
        //
        // The five calls behind the signed-in half of the panel (plan 10). All authenticated, all
        // total, all scoped entirely by the token - none of them sends a user identifier, because
        // the server accepts none. There is no admin surface to build against.

        /// <summary>
        /// The devices signed in to this account, in the order the server sent them. WL-141.
        /// </summary>
        /// <remarks>
        /// The order is the server's and is kept: most-recently-seen first, never-seen last. It
        /// pays an extra ORDER BY term to stop a device that has never made a request floating
        /// above the one the user is reading the list on, and re-sorting here would undo that.
        /// <para>
        /// <see cref="DeviceSummary.DeviceId"/> is the client's own per-install GUID - the same
        /// value stamped as <c>OriginDeviceId</c> on every row - and the only public identifier
        /// there is. The server's internal row id is never on the wire, exactly as synced entities
        /// are addressed by <c>SyncId</c> and never by their local <c>Id</c>.
        /// </para>
        /// </remarks>
        public async Task<DeviceListOutcome> GetDevicesAsync()
        {
            try
            {
                var devices = await SendForJsonAsync<List<DeviceSummary>>(
                    () => new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "auth/devices")));

                return DeviceListOutcome.Loaded(devices);
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider device list failed: {ex.Message}");
                return DeviceListOutcome.Failed(
                    DescribeAccountActionFailure(ex, "Couldn't load this account's devices."));
            }
        }

        /// <summary>
        /// Detaches one device from this account. WL-141.
        /// </summary>
        /// <remarks>
        /// <b>This removes the device's access and nothing else.</b> Projects, tasks and time
        /// entries belong to the <i>user</i>; <c>OriginDeviceId</c> on a row is provenance, not
        /// ownership. The server states this is the single most plausible wrong assumption about
        /// the endpoint, so it is worth repeating here: nothing a retired laptop recorded is
        /// deleted, and the laptop re-enrols by signing in again under the same
        /// <c>(UserId, DeviceId)</c>.
        /// <para>
        /// <b>Removing your OWN current device is allowed and is not a bug.</b> It means "sign this
        /// device out", and it is the only way to drop a token from the device holding it. The
        /// response is still a <c>204</c>; every request after it is a <c>401</c>, which this
        /// provider then reads as a revoked session - correctly, because it is one. Do not add a
        /// guard here: <see cref="DeviceSummary.IsCurrent"/> exists so a caller can warn, and a
        /// silent refusal would be this client overriding a deliberate server decision.
        /// </para>
        /// </remarks>
        public async Task<AccountActionOutcome> DeleteDeviceAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return AccountActionOutcome.Failed("No device was selected.");

            try
            {
                // 204, no body - hence the no-content send. EscapeDataString because this goes in
                // the path; a DeviceId is a GUID today and this costs nothing if it stays one.
                await SendForNoContentAsync(() => new HttpRequestMessage(
                    HttpMethod.Delete,
                    new Uri(_baseUri, $"auth/devices/{Uri.EscapeDataString(deviceId)}")));

                return AccountActionOutcome.Succeeded;
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider device removal failed: {ex.Message}");

                // A 404 is a real answer, not a transport problem: the device is unknown OR
                // belongs to someone else, and the server refuses to say which - distinguishing
                // them would confirm another account's device id to anyone willing to guess.
                // Either way it is already not on this account, which is what the caller wanted.
                return ex.StatusCode == HttpStatusCode.NotFound
                    ? AccountActionOutcome.Failed("That device isn't on this account any more.")
                    : AccountActionOutcome.Failed(
                        DescribeAccountActionFailure(ex, "Couldn't remove that device."));
            }
        }

        /// <summary>
        /// Signs every device out by rotating the account's security stamp, and keeps this one
        /// signed in with the fresh token that comes back. WL-142.
        /// </summary>
        /// <remarks>
        /// <b>Storing the returned token is not optional.</b> The stamp bump kills every
        /// outstanding token including this app's own, and the replacement in the response is the
        /// server making sure "sign my other devices out" does not sign out the device asking.
        /// Drop it and the feature looks like it logged the user out as a side effect.
        /// <para>
        /// Device rows survive - only tokens die, and each device gets back in by signing in
        /// again. <see cref="DeleteDeviceAsync"/> is what actually detaches one.
        /// </para>
        /// </remarks>
        public async Task<AccountActionOutcome> LogoutAllAsync()
        {
            try
            {
                // No body: the endpoint is scoped entirely by the token and takes no parameters,
                // so there is nothing to send and no request type for it in the contract.
                var response = await SendForJsonAsync<LoginResponse>(
                    () => new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "auth/logout-all")));

                AdoptFreshToken(response);
                return AccountActionOutcome.Succeeded;
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider logout-all failed: {ex.Message}");
                return AccountActionOutcome.Failed(
                    DescribeAccountActionFailure(ex, "Couldn't sign the other devices out."));
            }
        }

        /// <summary>
        /// Changes the account password, re-verifying the current one, and keeps this device
        /// signed in with the fresh token that comes back. WL-142.
        /// </summary>
        /// <remarks>
        /// The current password is re-checked even though this call already carries a valid token,
        /// on the server's insistence: a stolen token must not be enough to take an account over.
        /// <para>
        /// <b>A <c>401</c> here means the typed password was wrong, NOT that the session died</b> -
        /// see <c>credentialGuarded</c> on <c>SendAuthenticatedAsync</c> for how the two are told
        /// apart and why it matters. A <c>400</c> is the server's own password policy speaking, and
        /// its sentence is passed through rather than reconstructed from a rule this client
        /// deliberately does not hold.
        /// </para>
        /// </remarks>
        public async Task<AccountActionOutcome> ChangePasswordAsync(string currentPassword, string newPassword)
        {
            try
            {
                var response = await SendForJsonAsync<LoginResponse>(
                    () => BuildJsonRequest(
                        HttpMethod.Post, "auth/password", new ChangePasswordRequest(currentPassword, newPassword)),
                    credentialGuarded: true);

                AdoptFreshToken(response);
                return AccountActionOutcome.Succeeded;
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider password change failed: {ex.Message}");

                return ex.StatusCode switch
                {
                    HttpStatusCode.Unauthorized =>
                        AccountActionOutcome.Failed("That current password isn't right."),

                    // The server owns the password policy - length only, no composition rules - so
                    // its refusal is more accurate than anything this client could reconstruct.
                    HttpStatusCode.BadRequest => AccountActionOutcome.Failed(
                        ExtractServerError(ex) ?? "The server wouldn't accept that new password."),

                    _ => AccountActionOutcome.Failed(
                        DescribeAccountActionFailure(ex, "Couldn't change the password."))
                };
            }
        }

        /// <summary>
        /// Deletes the account and everything the server holds for it. WL-143.
        /// </summary>
        /// <remarks>
        /// <b>Immediate, total and irreversible</b> - a hard delete with no grace period and no
        /// undo. Both gates are the server's: <paramref name="password"/> because a leaked token
        /// must not be enough to destroy an account, and <paramref name="confirmEmail"/> because a
        /// typed confirmation independently stops a client pointed at the wrong session from
        /// deleting the wrong account.
        /// <para>
        /// <b>This touches nothing local.</b> The SQLite database, the pending <c>SyncQueue</c> and
        /// every project, task and entry in them are untouched by design - see the caller, which is
        /// where the session is stood down afterwards.
        /// </para>
        /// </remarks>
        public async Task<AccountActionOutcome> DeleteAccountAsync(string password, string confirmEmail)
        {
            try
            {
                // A DELETE with a body. Unusual, and the server's own comment explains why it has
                // to be declared [FromBody] there: minimal APIs refuse an inferred body on DELETE.
                await SendForNoContentAsync(
                    () => BuildJsonRequest(
                        HttpMethod.Delete, "auth/account", new DeleteAccountRequest(password, confirmEmail)),
                    credentialGuarded: true);

                return AccountActionOutcome.Succeeded;
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider account deletion failed: {ex.Message}");

                return ex.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => AccountActionOutcome.Failed("That password isn't right."),

                    // The email guard. Reachable only if the client-side check was skipped or the
                    // signed-in address moved underneath it, since the caller mirrors this locally
                    // to keep a typo away from the server's failed-password counter.
                    HttpStatusCode.BadRequest => AccountActionOutcome.Failed(
                        ExtractServerError(ex) ?? "That email address doesn't match this account."),

                    _ => AccountActionOutcome.Failed(
                        DescribeAccountActionFailure(ex, "Couldn't delete the account."))
                };
            }
        }

        // Both endpoints that rotate the security stamp hand back a replacement token for THIS
        // device. Storing it is what keeps the app signed in through an act that, server-side,
        // just invalidated every token the account had - this one included.
        private void AdoptFreshToken(LoginResponse response)
        {
            _tokens.Save(response.Token, response.ExpiresAtUtc);

            // The server just answered an authenticated request and minted a token, so whatever the
            // last verdict was, it is stale. Clearing it lets Auth derive SignedIn from the token
            // rather than leaving a revocation banner over a session that demonstrably works.
            _verdict = null;
        }

        // The shared tail of the five: the failures that mean the same thing whatever was being
        // asked for. Anything endpoint-specific is handled by the caller before this is reached.
        private static string DescribeAccountActionFailure(SyncProviderException ex, string fallback) => ex.StatusCode switch
        {
            // The session, not the request. The verdict has already been recorded by the time this
            // runs, so the panel will be showing a sign-in form by the time it is read.
            HttpStatusCode.Unauthorized => "You've been signed out - sign in again.",

            // A pre-user-accounts row with no password to re-verify. Practically unreachable, since
            // such an account cannot sign in in the first place.
            HttpStatusCode.Conflict => ExtractServerError(ex)
                ?? "This account has no password set yet - claim it first.",

            HttpStatusCode.TooManyRequests =>
                "The server is rate-limiting requests from this address. Try again shortly.",

            // No status code at all means nothing answered: a dead host, DNS, a timeout. Worth
            // keeping the transport's own words for, because it is the one failure worth retrying.
            null => ex.Message,

            _ => fallback
        };

        private static SyncChangeDto ToChangeDto(SyncQueue queueEntry)
        {
            // SyncQueue carries no Version/OriginDeviceId of its own - both live on the
            // serialized entity inside PayloadJson. Lifting them to the top level is the
            // same move FileSyncProvider.ToWireEntry makes, for the same reason: a receiver
            // can then run last-write-wins without deserializing per entity type.
            var (version, originDeviceId) = ExtractVersionMetadata(queueEntry.PayloadJson);

            return new SyncChangeDto(
                EntityType: queueEntry.EntityType ?? string.Empty,
                // Already the entity's SyncId, not the local autoincrement Id - see WL-47.
                EntityId: queueEntry.EntityId,
                Operation: queueEntry.Operation ?? string.Empty,
                PayloadJson: queueEntry.PayloadJson,
                Version: version,
                OriginDeviceId: originDeviceId ?? string.Empty,
                CreatedAt: queueEntry.CreatedAt);
        }

        private static RemoteChange ToRemoteChange(SyncChangeDto dto) => new()
        {
            EntityType = dto.EntityType,
            EntityId = dto.EntityId,
            Operation = dto.Operation,
            PayloadJson = dto.PayloadJson,
            Version = dto.Version,
            OriginDeviceId = dto.OriginDeviceId
        };

        private static (int Version, string? OriginDeviceId) ExtractVersionMetadata(string? payloadJson)
        {
            if (string.IsNullOrEmpty(payloadJson))
                return (0, null);

            try
            {
                var envelope = JsonSerializer.Deserialize<VersionEnvelope>(payloadJson, SyncJson.Options);
                return (envelope?.Version ?? 0, envelope?.OriginDeviceId);
            }
            catch (JsonException)
            {
                return (0, null);
            }
        }

        // WL-94: zip request and response by index. The response is guaranteed one entry per
        // input change in request order, so a count mismatch means the contract is broken
        // and there is no safe way to guess the alignment - fail the whole batch rather than
        // retire the wrong rows.
        private static SyncResult MapPushResults(List<SyncQueue> changes, SyncPushResponse response)
        {
            if (response.Results.Count != changes.Count)
            {
                throw new SyncProviderException(
                    $"Server returned {response.Results.Count} push results for {changes.Count} changes; " +
                    "results must be positional and one-per-change.");
            }

            var accepted = new List<int>();
            var rejected = new List<int>();
            var failures = new List<SyncFailure>();
            var authoritativeChanges = new List<RemoteChange>();
            var serverWon = false;

            for (var i = 0; i < changes.Count; i++)
            {
                var queueEntry = changes[i];
                var result = response.Results[i];

                if (result.Accepted)
                {
                    // Note there is no check that AuthoritativeVersion equals what we sent.
                    // An accepted delete legitimately comes back higher (the tombstone bump),
                    // and an accepted replay comes back at the version already stored.
                    accepted.Add(queueEntry.Id);
                    continue;
                }

                rejected.Add(queueEntry.Id);
                failures.Add(new SyncFailure
                {
                    SyncQueueId = queueEntry.Id,
                    ErrorMessage = result.Reason ?? "Rejected by server."
                });

                // An authoritative payload is how the server distinguishes "my row won" from
                // "your payload was invalid" - it only fills one in for the former. A validation
                // failure leaves the row retired and there is nothing to go and fetch.
                if (string.IsNullOrEmpty(result.AuthoritativePayloadJson))
                    continue;

                // WL-101: adopt the winner directly when the server says what it is. Falling
                // back to the cursor rewind when it doesn't is not caution for its own sake -
                // guessing "update" for a tombstone is the one mistake here that can't be
                // undone. See SyncResult.AuthoritativeChanges.
                var authoritative = ToAuthoritativeChange(queueEntry, result);
                if (authoritative is null)
                    serverWon = true;
                else
                    authoritativeChanges.Add(authoritative);
            }

            return new SyncResult
            {
                AcceptedSyncQueueIds = accepted,
                RejectedSyncQueueIds = rejected,
                AuthoritativeChanges = authoritativeChanges,
                RequiresFullPull = serverWon,
                Failures = failures
            };
        }

        // WL-101: shape a server-wins rejection into the same RemoteChange a pull would have
        // delivered, so the orchestrator has one merge path rather than two. Null when the
        // result names no operation the merge can act on, which is the caller's cue to rewind.
        //
        // Version comes from AuthoritativeVersion, not from the payload: for a tombstone the two
        // differ, since the server stores deletes at existing.Version + 1 (its WL-93
        // accommodation) while the payload is still the pre-delete row. EntityType and EntityId
        // come from the queue row we pushed - results are positional and SyncItemResult.EntityId
        // is not a usable key (WL-94) - and the operation is mapped onto this client's own
        // constants rather than passed through, so the merge compares like with like.
        private static RemoteChange? ToAuthoritativeChange(SyncQueue queueEntry, SyncItemResult result)
        {
            var isDelete = string.Equals(
                result.AuthoritativeOperation, LocalDbService.SyncOperations.Delete, StringComparison.OrdinalIgnoreCase);
            var isUpdate = string.Equals(
                result.AuthoritativeOperation, LocalDbService.SyncOperations.Update, StringComparison.OrdinalIgnoreCase);

            if (!isDelete && !isUpdate)
                return null;

            // The winner's author, lifted from the payload the same way a pushed change's is.
            // It matters: a winner this device authored is a self-echo the merge skips, exactly
            // as it would have been had the same row arrived through a pull.
            var (_, originDeviceId) = ExtractVersionMetadata(result.AuthoritativePayloadJson);

            return new RemoteChange
            {
                EntityType = queueEntry.EntityType ?? string.Empty,
                EntityId = queueEntry.EntityId,
                Operation = isDelete ? LocalDbService.SyncOperations.Delete : LocalDbService.SyncOperations.Update,
                PayloadJson = result.AuthoritativePayloadJson,
                Version = result.AuthoritativeVersion,
                OriginDeviceId = originDeviceId
            };
        }

        private HttpRequestMessage BuildJsonRequest(HttpMethod method, string relativePath, object body)
        {
            var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath))
            {
                // SyncJson.Options for the envelope too: it writes PascalCase, which the
                // server's own case-insensitive reader accepts, and reads case-insensitively,
                // so ASP.NET Core's camelCase responses parse. One policy, both directions.
                Content = new StringContent(
                    JsonSerializer.Serialize(body, body.GetType(), SyncJson.Options),
                    Encoding.UTF8,
                    "application/json")
            };
            return request;
        }

        // Sends with the bearer token attached and applies plan 8 item 4c's 401 rules.
        //
        // Note what is NOT here any more: the old proactive renewal that re-bootstrapped five
        // minutes ahead of expiry. Renewal now needs a password this client does not store, so
        // there is nothing left for it to call - and with tokens lasting a year, expiry is better
        // handled as the free SessionExpired check in Auth than as a margin.
        private Task<T> SendForJsonAsync<T>(Func<HttpRequestMessage> requestFactory, bool credentialGuarded = false)
            => SendAuthenticatedAsync(
                () => SendOnceForJsonAsync<T>(requestFactory, authorize: true), credentialGuarded);

        // WL-140: the same policy for an endpoint that answers 204 with an empty body.
        //
        // DELETE /auth/devices/{id} and DELETE /auth/account both do, and SendForJsonAsync would
        // fail them at the deserialize step - the request having already succeeded. Inventing an
        // empty response type to deserialize into would work and would be a lie about the wire.
        private Task SendForNoContentAsync(Func<HttpRequestMessage> requestFactory, bool credentialGuarded = false)
            => SendAuthenticatedAsync(
                async () =>
                {
                    await SendOnceAsync(requestFactory, authorize: true);

                    // A value only because the wrapper is generic; nothing reads it.
                    return true;
                },
                credentialGuarded);

        // Plan 8 item 4c's 401 rules, factored out so every authenticated call gets them
        // identically rather than by each remembering to.
        //
        // credentialGuarded is WL-142's one exception, and it is a real ambiguity on the wire
        // rather than a convenience. POST /auth/password and DELETE /auth/account re-verify the
        // password *inside the handler* and answer a wrong one with a 401 - the same status the
        // bearer middleware uses for a dead token. Two opposite meanings, one status code:
        //
        //   * the middleware's 401 means the SESSION is gone, and the app must show a sign-in form;
        //   * the handler's 401 means the TYPED PASSWORD was wrong, and the session is perfectly
        //     fine - routing it into the signed-out flow would sign the user out for a typo.
        //
        // They are told apart by the body. The server's own failures carry {"error":"..."} (its
        // AuthEndpoints.Error); a bearer challenge has an empty body and puts its reason in the
        // WWW-Authenticate header. So a 401 WITH a server error body on one of these two endpoints
        // is the guard, and it is rethrown untouched: no verdict, no login attempt.
        private async Task<T> SendAuthenticatedAsync<T>(Func<Task<T>> attempt, bool credentialGuarded = false)
        {
            // Whether the SERVER refused a token, or we simply never had one. The two look
            // identical from the catch below and mean opposite things to a user: "you were signed
            // out" versus "you were never signed in".
            var hadToken = !string.IsNullOrWhiteSpace(_tokens.Token);

            try
            {
                return await attempt();
            }
            catch (SyncProviderException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The guard failed, not the session. Say nothing about the auth state - see the
                // comment on credentialGuarded above.
                if (credentialGuarded && ExtractServerError(ex) is not null)
                    throw;

                if (hadToken)
                    _verdict = AuthStatus.SessionRevoked;

                // No credential held - the overwhelmingly common case, because the app holds one
                // only while someone is actually typing into the sign-in form. Report it and
                // stop. No login attempt: there is nothing to attempt with, and inventing a retry
                // here is exactly how a 15-minute timer locks a sleeping user out of their own
                // server. The queue rows stay pending; a human signs in; they go next time.
                var credential = CurrentCredential();
                if (credential is null)
                    throw;

                // A sign-in is in flight, so the session can legitimately be repaired mid-call.
                // Exactly once - TryLoginAsync discards the credential on a 401, so even a caller
                // that kept offering it could not produce a second attempt.
                var login = await TryLoginAsync();
                if (!login.Success)
                    throw;

                try
                {
                    return await attempt();
                }
                catch (SyncProviderException retryEx) when (retryEx.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // A token minted a moment ago and refused immediately. Retrying cannot help
                    // and another login would only spend an attempt on a credential the server
                    // just accepted.
                    _verdict = AuthStatus.SessionRevoked;
                    throw;
                }
            }
        }

        // One attempt, no renewal, and the response body as a string. A fresh HttpRequestMessage
        // per call because a message that has been sent cannot be sent again - hence the factory
        // rather than an instance parameter.
        //
        // Split from SendOnceForJsonAsync so a 204 with no body is reachable without pretending
        // there is JSON to parse: everything up to and including "did the server say yes" is the
        // same for both, and only the last step differs.
        private async Task<(HttpStatusCode StatusCode, string Body)> SendOnceAsync(
            Func<HttpRequestMessage> requestFactory, bool authorize = false)
        {
            using var request = requestFactory();

            if (authorize)
            {
                var token = _tokens.Token;
                if (string.IsNullOrWhiteSpace(token))
                    throw new SyncProviderException("Not signed in to the sync server.", HttpStatusCode.Unauthorized);

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request);
            }
            catch (HttpRequestException ex)
            {
                // Unreachable host, DNS failure, refused connection, TLS failure.
                throw new SyncProviderException($"Could not reach the sync server: {ex.Message}", innerException: ex);
            }
            catch (TaskCanceledException ex)
            {
                // HttpClient surfaces its own timeout as a cancellation.
                throw new SyncProviderException("The sync server did not respond in time.", innerException: ex);
            }

            using (response)
            {
                var body = await ReadBodyAsync(response);

                if (!response.IsSuccessStatusCode)
                {
                    throw new SyncProviderException(
                        $"Sync server returned {(int)response.StatusCode} {response.ReasonPhrase}." +
                        (string.IsNullOrWhiteSpace(body) ? string.Empty : $" {Truncate(body)}"),
                        response.StatusCode,
                        retryAfterUtc: ResolveRetryAfter(response),
                        responseBody: body);
                }

                return (response.StatusCode, body);
            }
        }

        private async Task<T> SendOnceForJsonAsync<T>(Func<HttpRequestMessage> requestFactory, bool authorize = false)
        {
            var (statusCode, body) = await SendOnceAsync(requestFactory, authorize);

            try
            {
                var parsed = JsonSerializer.Deserialize<T>(body, SyncJson.Options);
                if (parsed is null)
                    throw new SyncProviderException("Sync server returned an empty response body.", statusCode);

                return parsed;
            }
            catch (JsonException ex)
            {
                throw new SyncProviderException(
                    $"Sync server returned a response that could not be parsed: {ex.Message}",
                    statusCode,
                    ex);
            }
        }

        private static async Task<string> ReadBodyAsync(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                // A connection that dies mid-body is still just an unreachable server.
                throw new SyncProviderException($"Could not read the sync server's response: {ex.Message}", response.StatusCode, ex);
            }
        }

        // WL-134: the 423 and 429 the credential endpoints send both carry a Retry-After, in
        // either of the two forms RFC 9110 allows. Resolving it to an instant here means the auth
        // state can say when a wait lifts, and can lift itself without anyone asking.
        private static DateTime? ResolveRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter is null)
                return null;

            return retryAfter.Delta is { } delta ? DateTime.UtcNow + delta : retryAfter.Date?.UtcDateTime;
        }

        // Server error bodies land in user-facing toasts (Plan 7); an HTML error page would
        // otherwise arrive in full.
        private static string Truncate(string value, int maxLength = 200)
            => value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";

        private class VersionEnvelope
        {
            public int Version { get; set; }
            public string? OriginDeviceId { get; set; }
        }
    }
}
