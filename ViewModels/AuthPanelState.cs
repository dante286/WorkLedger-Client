using System;
using WorkLedger.Contracts.Auth;
using WorkLedger_Client.Sync;

namespace WorkLedger_Client.ViewModels
{
    // WL-146, plan 9 item 2. Everything the API sync panel shows, as one projection of
    // (AuthStatus, AuthCapabilitiesResponse) - rather than a handful of booleans on the view model
    // that can quietly disagree with each other.
    //
    // It is a plain class with no Avalonia in it, deliberately. The rules below are the interesting
    // part of this plan - when Sign in must be refused, what each auth state says, what a repeated
    // failure is allowed to say - and putting them behind a view model that needs a
    // dispatcher, a database and a theme service to construct would make them the part nobody tests.
    //
    // The copy here is the PANEL's: persistent, and able to take a sentence to explain itself.
    // SyncCoordinator.DescribeAuthState is the same states rendered for a toast, where one short
    // line is the whole budget. Two surfaces, two registers; keep them in step.
    public sealed class AuthPanelState
    {
        // Plan 9 item 2a: warn from the third consecutive failure, not from the threshold.
        //
        // The server's FailedLoginCount is per USER, so a phone and a laptop spend one shared
        // budget and this install's count is a lower bound on the server's, never the truth. Warning
        // early is the only honest response to a number you know is too low.
        public const int FailureWarningThreshold = 3;

        private AuthPanelState()
        {
        }

        public AuthState State { get; private init; }

        public bool IsSignedIn { get; private init; }

        // The one affordance that is available in EVERY combination, including a server with
        // registration switched off. Gating sign-in on the registration flag would lock every
        // existing user out of a server whose owner shut the door behind them.
        public bool ShowSignIn => true;

        // Plan 9 item 2: while locked out the Sign in button is DISABLED, not merely discouraged.
        // The server refuses the correct password for the duration, so an enabled button can only
        // produce another 423 and spend the per-IP rate limit that every other device behind this
        // address shares. This is the one place the UI is load-bearing for the lockout design
        // rather than just reporting it.
        public bool IsLockedOut { get; private init; }

        public DateTime? LockedUntilUtc { get; private init; }

        public string StatusMessage { get; private init; } = string.Empty;

        public static AuthPanelState From(
            AuthStatus auth,
            AuthCapabilitiesResponse? capabilities,
            string? signedInEmail,
            string? displayName = null,
            DateTime? sessionExpiresAtUtc = null)
        {
            return new AuthPanelState
            {
                State = auth.State,
                IsSignedIn = auth.State == AuthState.SignedIn,
                IsLockedOut = auth.State == AuthState.LockedOut && !auth.HasLifted,
                LockedUntilUtc = auth.RetryAfterUtc,
                StatusMessage = Describe(auth, signedInEmail, displayName, sessionExpiresAtUtc)
            };
        }

        private static string Describe(
            AuthStatus auth, string? email, string? displayName, DateTime? sessionExpiresAtUtc) => auth.State switch
        {
            AuthState.SignedIn => DescribeSignedIn(email, displayName, sessionExpiresAtUtc),

            AuthState.SignedOut => string.IsNullOrWhiteSpace(email)
                ? "Not signed in. Sign in to sync with this server."
                : $"Not signed in. Sign in as {email} to resume syncing.",

            // Reached locally from a stale ApiTokenExpiresAtUtc, with no request at all, so it can
            // be on screen the moment the panel opens rather than after a failed sync.
            AuthState.SessionExpired => sessionExpiresAtUtc is { } expired
                ? $"Your session expired on {expired.ToLocalTime():g}. Sign in to resume syncing."
                : "Your session has expired. Sign in to resume syncing.",

            // Plan 9 item 6. The causes are named because a revoked session is otherwise
            // indistinguishable from a bug, and none of them is guessable from here - the server
            // does not say which, and asking would leak more than it tells.
            AuthState.SessionRevoked =>
                "You've been signed out by the server. That usually means the password was changed, "
                + "this device was removed from another machine, or the account was deleted. "
                + "Sign in again to resume syncing - your work is safe on this device.",

            AuthState.LockedOut =>
                "Too many failed sign-in attempts, so this account is temporarily locked. "
                + "The server refuses even the correct password until the lock lifts.",

            // Worded about the address rather than the account: this limiter is per-IP and shared
            // with registration, so it may have been earned by another device on the same network.
            AuthState.RateLimited =>
                "The server is rate-limiting sign-in attempts from this address. Try again shortly.",

            _ => string.Empty
        };

        private static string DescribeSignedIn(string? email, string? displayName, DateTime? expiresAtUtc)
        {
            var who = string.IsNullOrWhiteSpace(displayName)
                ? (string.IsNullOrWhiteSpace(email) ? "Signed in" : $"Signed in as {email}")
                : $"Signed in as {displayName} ({email})";

            // An upper bound, not a promise: the server can revoke this long before it lapses. Shown
            // as context beside the identity rather than as a countdown to anything to act on.
            return expiresAtUtc is { } expiry ? $"{who} · session valid until {expiry.ToLocalTime():g}" : who;
        }

        /// <summary>
        /// "Try again in 14:32", for the disabled Sign in button. Empty once the lock has lifted.
        /// </summary>
        public static string DescribeLockoutCountdown(DateTime? lockedUntilUtc, DateTime nowUtc)
        {
            if (lockedUntilUtc is not { } until)
                return string.Empty;

            var remaining = until - nowUtc;
            return remaining <= TimeSpan.Zero ? string.Empty : $"Try again in {remaining:mm\\:ss}";
        }

        /// <summary>
        /// Plan 9 item 2a: the warning shown <em>before</em> the next attempt is sent, from the third
        /// consecutive failure onwards. Empty below the threshold.
        /// </summary>
        /// <remarks>
        /// Names the server's real numbers when it reports them and stays generic when it does not.
        /// Never hardcodes 5 and 15: they are <c>Auth:MaxFailedLogins</c> and
        /// <c>Auth:LockoutMinutes</c>, an operator may have changed either, and a warning that
        /// states the wrong threshold is worse than one that states none.
        /// </remarks>
        public static string DescribeRepeatedFailureWarning(int consecutiveFailures, AuthCapabilitiesResponse? capabilities)
        {
            if (consecutiveFailures < FailureWarningThreshold)
                return string.Empty;

            var generic = $"{consecutiveFailures} failed sign-in attempts so far. "
                + "Too many in a row will lock the account for a while, and the lock refuses the "
                + "correct password too.";

            if (capabilities is not { MaxFailedLogins: > 0, LockoutMinutes: > 0 } policy)
                return generic;

            var remaining = Math.Max(0, policy.MaxFailedLogins - consecutiveFailures);
            var minutes = $"{policy.LockoutMinutes} minute{(policy.LockoutMinutes == 1 ? string.Empty : "s")}";

            // "At most", because the server counts per user: another device's attempts have already
            // spent some of this budget and this install cannot see them.
            return remaining <= 0
                ? $"The next failed attempt may lock the account for {minutes}."
                : $"At most {remaining} more attempt{(remaining == 1 ? string.Empty : "s")} before the "
                  + $"account locks for {minutes} - and a lock refuses the correct password too.";
        }
    }
}
