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
            int lockoutMinutes = 15,
            string? portalUrl = null)
            => new(registrationEnabled, requiresSecret, minPasswordLength, hasUnclaimedAccount, maxFailedLogins, lockoutMinutes,
                PortalUrl: portalUrl);

        private const string Portal = "https://ledger.example.com/portal";

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

        // WL-176, plan 12 item 6. "I've reset my password" exists to undo exactly one thing - a
        // lockout verdict a reset elsewhere has made stale - so it is offered for exactly as long as
        // that verdict is disabling Sign in, and at no other time.
        [Fact]
        public void TheResetEscape_IsOfferedOnlyWhileLockedOut()
        {
            Assert.True(AuthPanelState.From(
                AuthStatus.LockedOut(DateTime.UtcNow.AddMinutes(15)), Capabilities(portalUrl: Portal), Email)
                .ShowResetPasswordEscape);

            // A lockout with no Retry-After still disables Sign in, so it still gets the way out.
            Assert.True(AuthPanelState.From(AuthStatus.LockedOut(null), Capabilities(), Email).ShowResetPasswordEscape);

            var notLocked = new[]
            {
                AuthStatus.LockedOut(DateTime.UtcNow.AddSeconds(-1)), // lifted by itself
                AuthStatus.SignedOut, AuthStatus.SignedIn, AuthStatus.SessionExpired,
                AuthStatus.SessionRevoked, AuthStatus.RateLimited(DateTime.UtcNow.AddMinutes(1))
            };

            foreach (var status in notLocked)
                Assert.False(AuthPanelState.From(status, Capabilities(portalUrl: Portal), Email).ShowResetPasswordEscape);
        }

        // ------------------------------------------------------------ the web portal (plan 12)

        [Fact]
        public void PortalUrl_ComesFromTheCapabilities_ExactlyAsAdvertised()
        {
            var panel = AuthPanelState.From(AuthStatus.SignedOut, Capabilities(portalUrl: Portal), Email);

            Assert.Equal(Portal, panel.PortalUrl);
        }

        [Fact]
        public void PortalUrl_IsNull_WhenThereAreNoCapabilities()
        {
            // An unreachable server, or one predating /auth/capabilities.
            var panel = AuthPanelState.From(AuthStatus.SignedOut, capabilities: null, signedInEmail: Email);

            Assert.Null(panel.PortalUrl);
            Assert.False(panel.ShowOpenPortal);
            Assert.False(panel.ShowManageAccount);
        }

        [Fact]
        public void PortalUrl_IsNull_FromAPreS11Server()
        {
            // The six-field construction a pre-S11 response deserializes to - PortalUrl defaulted.
            var preS11 = new AuthCapabilitiesResponse(true, true, 12, false, 5, 15);
            var panel = AuthPanelState.From(AuthStatus.SignedOut, preS11, Email);

            Assert.Null(panel.PortalUrl);
            Assert.False(panel.ShowOpenPortal);

            // A blank value is no portal either, rather than a button that opens nothing.
            Assert.Null(AuthPanelState.From(AuthStatus.SignedOut, Capabilities(portalUrl: "  "), Email).PortalUrl);
        }

        [Fact]
        public void SignedOut_WithAPortal_SaysAccountsComeFromItsInvites_AndOffersToOpenIt()
        {
            var panel = AuthPanelState.From(AuthStatus.SignedOut, Capabilities(portalUrl: Portal), Email);

            Assert.True(panel.ShowOpenPortal);
            Assert.False(panel.ShowManageAccount);
            Assert.Contains("web portal", panel.PortalHint);

            // The S11 handoff's point: the portal has no open sign-up, so the copy must not promise
            // one. An account comes from an administrator's invite.
            Assert.Contains("invite", panel.PortalHint);
        }

        [Fact]
        public void SignedOut_WithoutAPortal_PointsAtTheAdministrator_AndOffersNoButton()
        {
            var panel = AuthPanelState.From(AuthStatus.SignedOut, Capabilities(), Email);

            Assert.False(panel.ShowOpenPortal);
            Assert.False(panel.ShowManageAccount);
            Assert.Contains("administrator", panel.PortalHint);
            Assert.DoesNotContain("portal", panel.PortalHint);
        }

        [Fact]
        public void SignedIn_OffersManageAccount_AndNoNoAccountHint()
        {
            var panel = AuthPanelState.From(AuthStatus.SignedIn, Capabilities(portalUrl: Portal), Email);

            Assert.True(panel.ShowManageAccount);
            Assert.False(panel.ShowOpenPortal);
            Assert.Equal(string.Empty, panel.PortalHint);
        }

        [Fact]
        public void ARevokedSession_OffersManageAccount()
        {
            // The likeliest cause of a revocation now is something done in the portal - a password
            // change, this device removed - and the portal is where to find out which.
            var panel = AuthPanelState.From(AuthStatus.SessionRevoked, Capabilities(portalUrl: Portal), Email);

            Assert.True(panel.ShowManageAccount);
            Assert.False(panel.ShowOpenPortal);
            Assert.Equal(string.Empty, panel.PortalHint);
        }

        [Fact]
        public void ALockout_StillOffersThePortal_WhereAResetLinkIsRedeemed()
        {
            var panel = AuthPanelState.From(
                AuthStatus.LockedOut(DateTime.UtcNow.AddMinutes(15)), Capabilities(portalUrl: Portal), Email);

            Assert.True(panel.ShowOpenPortal);
            Assert.True(panel.ShowResetPasswordEscape);
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
            // Plan 12 item 5: these are now the normal path for account changes made in a browser,
            // so the copy names the reset and the web portal as well.
            Assert.Contains("this device is removed", revoked.StatusMessage);
            Assert.Contains("reset", revoked.StatusMessage);
            Assert.Contains("web portal", revoked.StatusMessage);
            Assert.Contains("your work is safe on this device", revoked.StatusMessage, StringComparison.OrdinalIgnoreCase);

            // And the lockout points at the only way out of a forgotten password.
            Assert.Contains("reset link", locked.StatusMessage);

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
