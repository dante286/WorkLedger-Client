using System;
using WorkLedger.Contracts.Auth;
using WorkLedger_Client.Sync;
using WorkLedger_Client.ViewModels;
using Xunit;

namespace WorkLedger.Tests
{
    // WL-146, plan 9. The rules that decide what the auth panel offers, tested without Avalonia.
    //
    // The point of pulling AuthPanelState out of MainWindowViewModel was to make exactly these
    // assertions cheap: when Sign in must be refused outright, what each auth state says, and what
    // a repeated failure is allowed to say. All three are things a wrong answer makes either
    // unusable or unsafe, and none of them needs a dispatcher to check.
    public class AuthPanelStateTests
    {
        private const string Email = "person@example.com";

        private static AuthCapabilitiesResponse Capabilities(
            bool registrationEnabled = true,
            bool requiresSecret = true,
            int minPasswordLength = 12,
            bool hasUnclaimedAccount = false,
            int maxFailedLogins = 5,
            int lockoutMinutes = 15)
            => new(registrationEnabled, requiresSecret, minPasswordLength, hasUnclaimedAccount, maxFailedLogins, lockoutMinutes);

        // ------------------------------------------------------------------ sign-in is unconditional

        [Fact]
        public void SignIn_IsOffered_WhateverTheServerReports()
        {
            // Plan 12 removed the registration and claim affordances, and with them the reason this
            // used to be a visibility matrix. What survives is the half that mattered most: a server
            // with registration closed still lets its existing users in, and so does one that
            // predates /auth/capabilities or cannot be reached right now (null capabilities).
            var closed = AuthPanelState.From(
                AuthStatus.SignedOut, Capabilities(registrationEnabled: false), Email);
            var unknown = AuthPanelState.From(AuthStatus.SignedOut, capabilities: null, signedInEmail: Email);

            Assert.True(closed.ShowSignIn);
            Assert.True(unknown.ShowSignIn);
            Assert.False(unknown.IsLockedOut);
        }

        // ------------------------------------------------------------------ the lockout, in the UI

        [Fact]
        public void LockedOut_DisablesSignIn_AndSaysWhenItLifts()
        {
            var until = DateTime.UtcNow.AddMinutes(15);
            var panel = AuthPanelState.From(AuthStatus.LockedOut(until), Capabilities(), Email);

            // This is the one place the UI is load-bearing for the lockout design rather than just
            // reporting it: the server refuses the correct password while locked, so an enabled
            // button could only earn another 423 and burn the shared per-IP limit.
            Assert.True(panel.IsLockedOut);
            Assert.Equal(until, panel.LockedUntilUtc);

            var countdown = AuthPanelState.DescribeLockoutCountdown(until, DateTime.UtcNow);
            Assert.StartsWith("Try again in 14:", countdown);
        }

        [Fact]
        public void AnExpiredLockout_ReEnablesSignIn()
        {
            var lifted = DateTime.UtcNow.AddSeconds(-1);
            var panel = AuthPanelState.From(AuthStatus.LockedOut(lifted), Capabilities(), Email);

            Assert.False(panel.IsLockedOut);
            Assert.Equal(string.Empty, AuthPanelState.DescribeLockoutCountdown(lifted, DateTime.UtcNow));
        }

        // -------------------------------------------------------------- one message per state

        [Fact]
        public void EachSignedOutState_GetsItsOwnMessage()
        {
            var expired = AuthPanelState.From(
                AuthStatus.SessionExpired, Capabilities(), Email, sessionExpiresAtUtc: DateTime.UtcNow.AddDays(-2));
            var revoked = AuthPanelState.From(AuthStatus.SessionRevoked, Capabilities(), Email);
            var locked = AuthPanelState.From(AuthStatus.LockedOut(DateTime.UtcNow.AddMinutes(15)), Capabilities(), Email);
            var limited = AuthPanelState.From(AuthStatus.RateLimited(DateTime.UtcNow.AddMinutes(1)), Capabilities(), Email);
            var signedOut = AuthPanelState.From(AuthStatus.SignedOut, Capabilities(), Email);

            var messages = new[]
            {
                expired.StatusMessage, revoked.StatusMessage, locked.StatusMessage,
                limited.StatusMessage, signedOut.StatusMessage
            };

            // Five states, five messages. Collapsing any two costs the user a guess about what to
            // do next, which is the entire reason the state enum has five members.
            Assert.Equal(messages.Length, System.Linq.Enumerable.Count(System.Linq.Enumerable.Distinct(messages)));

            Assert.Contains("expired", expired.StatusMessage, StringComparison.OrdinalIgnoreCase);

            // Plan 9 item 6: name the plausible causes, because a revoked session is otherwise
            // indistinguishable from a bug - and reassure, because the local work is untouched.
            Assert.Contains("removed from another machine", revoked.StatusMessage);
            Assert.Contains("your work is safe on this device", revoked.StatusMessage, StringComparison.OrdinalIgnoreCase);

            // The rate limit is about the address, not the account - it may have been earned by a
            // different device on the same network.
            Assert.Contains("address", limited.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ARevokedSession_KeepsShowingTheSignInFormWithTheEmailItKnows()
        {
            var panel = AuthPanelState.From(AuthStatus.SessionRevoked, Capabilities(), Email);

            Assert.False(panel.IsSignedIn);
            Assert.True(panel.ShowSignIn);
            Assert.False(panel.IsLockedOut);
        }

        [Fact]
        public void SignedIn_ShowsTheDisplayNameWhenThereIsOne()
        {
            var withName = AuthPanelState.From(AuthStatus.SignedIn, Capabilities(), Email, "A Person");
            Assert.Contains("A Person", withName.StatusMessage);
            Assert.Contains(Email, withName.StatusMessage);

            var withoutName = AuthPanelState.From(AuthStatus.SignedIn, Capabilities(), Email);
            Assert.Contains(Email, withoutName.StatusMessage);
        }

        // ------------------------------------------------ the repeated-failure warning (item 2a)

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void BelowTheThreshold_NothingIsSaid(int failures)
            => Assert.Equal(string.Empty, AuthPanelState.DescribeRepeatedFailureWarning(failures, Capabilities()));

        [Fact]
        public void FromTheThirdFailure_TheWarningNamesTheServersRealNumbers()
        {
            // Warning at three rather than at five is deliberate: FailedLoginCount is per USER, so
            // another machine's attempts spend the same budget and this count is a lower bound.
            var warning = AuthPanelState.DescribeRepeatedFailureWarning(3, Capabilities(maxFailedLogins: 5, lockoutMinutes: 15));

            Assert.Contains("2 more attempts", warning);
            Assert.Contains("15 minutes", warning);
        }

        [Fact]
        public void TheWarningUsesTheServersPolicy_NotHardcodedFivesAndFifteens()
        {
            // An operator may have changed Auth:MaxFailedLogins / Auth:LockoutMinutes, and a warning
            // that confidently states the wrong threshold is worse than one that states none.
            var warning = AuthPanelState.DescribeRepeatedFailureWarning(3, Capabilities(maxFailedLogins: 4, lockoutMinutes: 30));

            Assert.Contains("1 more attempt", warning);
            Assert.Contains("30 minutes", warning);
            Assert.DoesNotContain("15 minutes", warning);
        }

        [Fact]
        public void WithoutAPolicyToQuote_TheWarningStaysGeneric()
        {
            // A server that reports no lockout policy - or none at all - gets honest vagueness
            // rather than invented specifics.
            var noCapabilities = AuthPanelState.DescribeRepeatedFailureWarning(3, capabilities: null);
            var noPolicy = AuthPanelState.DescribeRepeatedFailureWarning(
                3, Capabilities(maxFailedLogins: 0, lockoutMinutes: 0));

            foreach (var warning in new[] { noCapabilities, noPolicy })
            {
                Assert.NotEqual(string.Empty, warning);
                Assert.DoesNotContain("5", warning);
                Assert.DoesNotContain("15 minutes", warning);
            }
        }
    }
}
