using System;
using System.Collections.Generic;
using WorkLedger.Contracts.Auth;

namespace WorkLedger_Client.Sync
{
    // WL-134, plan 8 item 4a. One auth state, derived in one place, so the whole app switches
    // on this rather than on a scatter of booleans that can disagree with each other.
    //
    // The distinctions are not decoration - each one wants a different thing from the user:
    // "sign in", "sign in again", "you were signed out elsewhere", "wait", "wait, and it isn't
    // about you". Collapsing any two of them into "sync failed" is what produces a support
    // question instead of an action.
    public enum AuthState
    {
        // No token at all: a fresh install, or a sign-out. The one state that is entirely
        // normal and needs no explanation beyond a sign-in form.
        SignedOut,

        // A token that has not been refused and has not visibly aged out. Note this is the
        // strongest claim the client can make without asking: the server can have revoked it
        // seconds ago and nothing here would know until the next request.
        SignedIn,

        // ApiTokenExpiresAtUtc is in the past. Reached for free, without any request, which is
        // the point - a year-idle install should prompt rather than fail a sync first.
        SessionExpired,

        // A 401 on an authenticated call we had every reason to believe would work. Since the
        // server's S7 a token carries a security stamp, so this means the session was killed:
        // a password change, a sign-out-everywhere, this device removed from another machine,
        // or the account deleted. Deliberately not persisted - see ApiSyncProvider.
        SessionRevoked,

        // 423 from POST /auth/login: too many consecutive failed sign-ins, and the account
        // refuses the *correct* password until the window lifts. Reachable only from an
        // interactive sign-in, because nothing else in this client ever calls /auth/login.
        LockedOut,

        // 429: the server's per-IP rate limit on the credential endpoints. A different thing
        // from a lockout and worth saying differently - nobody's account is in trouble, the
        // caller is just early.
        RateLimited
    }

    // An AuthState plus the instant a self-clearing one lifts. A record rather than a bare
    // enum because "locked out" without "until when" is the version of this message that sends
    // people to look for an administrator who has nothing to do.
    public sealed record AuthStatus(AuthState State, DateTime? RetryAfterUtc = null)
    {
        public static readonly AuthStatus SignedOut = new(AuthState.SignedOut);
        public static readonly AuthStatus SignedIn = new(AuthState.SignedIn);
        public static readonly AuthStatus SessionExpired = new(AuthState.SessionExpired);
        public static readonly AuthStatus SessionRevoked = new(AuthState.SessionRevoked);

        public static AuthStatus LockedOut(DateTime? until) => new(AuthState.LockedOut, until);

        public static AuthStatus RateLimited(DateTime? until) => new(AuthState.RateLimited, until);

        // What the background timer asks before deciding whether a run is worth the round trip
        // (plan 8 item 4d). Anything but SignedIn means the request is already known to fail.
        public bool CanSync => State == AuthState.SignedIn;

        // LockedOut and RateLimited lift by themselves, so they are the only states that stop
        // being true without anyone doing anything. Everything else waits for a person.
        public bool HasLifted => RetryAfterUtc is { } until && until <= DateTime.UtcNow;
    }

    // WL-135: what a sign-in needs, held for exactly as long as the call takes.
    //
    // It reaches ApiSyncProvider through a Func<ApiCredentials?> rather than as a constructor
    // string, for the same reason the bootstrap secret did before it: the provider outlives the
    // sign-in, the credential must not, and an accessor lets the caller stop answering. It is
    // also the seam an OS credential store would implement later without touching anything
    // else - see AppSettings.ApiEmail for why that is deliberately not this release.
    public sealed record ApiCredentials(string Email, string Password);

    // What one sign-in attempt did.
    //
    // <see cref="User"/> is carried back rather than left to a follow-up /auth/whoami because
    // LoginResponse already includes it - and because the account-ownership guard (WL-138) needs
    // the server's UserId at exactly this moment, before any data moves. A caller that can't tell
    // who it just signed in as can't tell whether this database belongs to them.
    //
    // ErrorMessage is written for a person to read, not parsed: the auth state is where a caller
    // looks to decide what to *do*.
    public sealed record LoginOutcome(bool Success, UserSummary? User, string? ErrorMessage);

    // WL-146: what one registration or claim did.
    //
    // Carries no token, and that is the server's shape rather than an omission here: tokens are
    // device-scoped and only POST /auth/login mints one, while creating an account is a user-level
    // act with no device bound to it. A caller that wants to end up signed in must follow a success
    // with a login - which the coordinator does automatically, because making someone type the
    // credentials they just chose a second time is worse than one extra round trip.
    //
    // CapabilitiesStale says the cached AuthCapabilitiesResponse is describing a server that has
    // moved on - registration switched off, or the last claimable account claimed - so the caller
    // should re-probe rather than leave a form on screen that can only fail the same way twice.
    public sealed record AccountOutcome(
        bool Success, UserSummary? User, string? ErrorMessage, bool CapabilitiesStale = false);

    // WL-140, plan 10: what one account-management call did.
    //
    // Deliberately says nothing about WHAT was managed. Removing a device, changing a password,
    // signing every device out and deleting the account are four very different acts with exactly
    // the same two possible outcomes from a caller's point of view - it worked, or here is a
    // sentence saying why it didn't - and giving each its own result type would be four types
    // distinguishable only by their names.
    //
    // ErrorMessage is written for a person to read. Where the server explains itself better than
    // this client could (a rejected new password, a device that isn't on the account), it is the
    // server's own sentence.
    public sealed record AccountActionOutcome(bool Success, string? ErrorMessage)
    {
        public static readonly AccountActionOutcome Succeeded = new(true, null);

        public static AccountActionOutcome Failed(string message) => new(false, message);
    }

    // WL-141: the account's devices, or the reason there aren't any to show.
    //
    // Success is separate from an empty list for the same reason PullResult.Succeeded is: a
    // server that cannot be reached and an account with nothing on it are not the same fact, and
    // a UI that renders "no devices" for a failed request is lying about the account.
    //
    // The order is the SERVER's - most recently seen first, never-seen last - and callers keep
    // it. Re-sorting here would fight a decision the server made deliberately (see its
    // AccountEndpoints.MapListDevices, which pays an extra ORDER BY term for exactly this).
    public sealed record DeviceListOutcome(
        bool Success, IReadOnlyList<DeviceSummary> Devices, string? ErrorMessage)
    {
        public static DeviceListOutcome Loaded(IReadOnlyList<DeviceSummary> devices)
            => new(true, devices, null);

        public static DeviceListOutcome Failed(string message)
            => new(false, Array.Empty<DeviceSummary>(), message);
    }

    // Implemented by a provider that authenticates, which today is only ApiSyncProvider -
    // FileSyncProvider syncs through a folder and NullSyncProvider does nothing, and neither
    // has a session to lose.
    //
    // Separate from ISyncProvider on purpose: SyncOrchestrator asks for this and does nothing
    // when it isn't there, so "does this topology even have a sign-in?" stays a question the
    // type system answers rather than a null check on a property nobody else can fill in.
    public interface IAuthenticatingSyncProvider : ISyncProvider
    {
        AuthStatus Auth { get; }
    }
}
