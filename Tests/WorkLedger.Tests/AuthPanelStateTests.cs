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
    // assertions cheap: which affordances a server allows, when Sign in must be refused outright,
    // and what a repeated failure is allowed to say. All three are things a wrong answer makes
    // either unusable or unsafe, and none of them needs a dispatcher to check.
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

        // ------------------------------------------------------------ the visibility matrix

        [Fact]
        public void RegistrationDisabled_HidesTheRegistrationUi_ButKeepsSignInAvailable()
        {
            // The whole reason GET /auth/capabilities exists (server S6 item 8). A client that only
            // learns the flag from a 403 has already shown a form that could never work.
            var panel = AuthPanelState.From(
                AuthStatus.SignedOut, Capabilities(registrationEnabled: false), Email);

            Assert.False(panel.ShowCreateAccount);

            // And the other half, which matters just as much: a closed server still lets its
            // existing users in. Gating sign-in on the registration flag would lock everyone out of
            // a server whose owner shut the door behind them.
            Assert.True(panel.ShowSignIn);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheSignupSecretField_FollowsWhatTheServerSaysItNeeds(bool requiresSecret)
        {
            var panel = AuthPanelState.From(AuthStatus.SignedOut, Capabilities(requiresSecret: requiresSecret), Email);

            Assert.Equal(requiresSecret, panel.ShowSignupSecret);
        }

        [Fact]
        public void ClaimIsOffered_OnlyWhenTheServerHasAnUnclaimedAccount()
        {
            Assert.False(AuthPanelState.From(
                AuthStatus.SignedOut, Capabilities(hasUnclaimedAccount: false), Email).ShowClaimServer);

            Assert.True(AuthPanelState.From(
                AuthStatus.SignedOut, Capabilities(hasUnclaimedAccount: true), Email).ShowClaimServer);
        }

        [Fact]
        public void ClaimIsNotGatedOnRegistrationBeingEnabled()
        {
            // An operator who closed signups must still be able to take ownership of their own
            // existing data - the server agrees, and does not gate /auth/claim on the flag either.
            var panel = AuthPanelState.From(
                AuthStatus.SignedOut,
                Capabilities(registrationEnabled: false, hasUnclaimedAccount: true),
                Email);

            Assert.False(panel.ShowCreateAccount);
            Assert.True(panel.ShowClaimServer);
        }

        [Fact]
        public void NoCapabilities_DegradesToSignInOnly_RatherThanToAnErrorState()
        {
            // Null is what a server predating /auth/capabilities returns, and what an unreachable
            // one leaves behind. Both are supported things to point at.
            var panel = AuthPanelState.From(AuthStatus.SignedOut, capabilities: null, signedInEmail: Email);

            Assert.True(panel.ShowSignIn);
            Assert.False(panel.ShowCreateAccount);
            Assert.False(panel.ShowClaimServer);
            Assert.False(panel.ShowSignupSecret);

            // And no password rule of our own, because there is nothing to base one on.
            Assert.Equal(0, panel.MinPasswordLength);
        }

        [Fact]
        public void WhileSignedIn_NeitherAccountAffordanceIsOffered()
        {
            // A second identity has nowhere to go: the local database holds exactly one owner.
            var panel = AuthPanelState.From(
                AuthStatus.SignedIn, Capabilities(hasUnclaimedAccount: true), Email);

            Assert.False(panel.ShowCreateAccount);
            Assert.False(panel.ShowClaimServer);
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

        // ------------------------------------------ WL-143/WL-144: the account-deletion confirmation

        [Fact]
        public void TheDeletionConfirmation_MakesItsFourStatementsInOrder()
        {
            // The ORDER is the specification, not a preference. Someone reading a destructive
            // confirmation reads the top and skims the rest, so the two facts that cannot be
            // recovered from go first and the reassurance goes after them. Leading with "your local
            // data is kept" would have people stop at the reassurance and miss that the server's
            // copy goes permanently.
            var text = AuthPanelState.DescribeAccountDeletion(Email);

            var immediate = text.IndexOf("permanently and immediately", StringComparison.Ordinal);
            var noUndo = text.IndexOf("no undo", StringComparison.Ordinal);
            var localKept = text.IndexOf("is kept", StringComparison.Ordinal);
            var export = text.IndexOf("Export CSV", StringComparison.Ordinal);

            Assert.True(immediate >= 0 && noUndo >= 0 && localKept >= 0 && export >= 0);
            Assert.True(immediate < noUndo, "the permanence must come before the no-undo");
            Assert.True(noUndo < localKept, "the no-undo must come before the reassurance");
            Assert.True(localKept < export, "the reassurance must come before the export advice");
        }

        [Fact]
        public void TheDeletionConfirmation_NamesTheAccountAndPointsAtTheLocalExport()
        {
            var text = AuthPanelState.DescribeAccountDeletion(Email);

            // Named, so nobody deletes the wrong account from a stale panel.
            Assert.Contains(Email, text);

            // The Reports tab's own CSV export, which is already in the app. The server's
            // /reports/*?format=csv is deliberately NOT offered instead: this client does not
            // consume the server's report endpoints at all, and an account about to stop existing
            // is the worst possible moment to introduce a second export path.
            Assert.Contains("Reports tab", text);

            // And the thing that makes the whole feature survivable.
            Assert.Contains("Nothing local is deleted", text);
        }

        [Fact]
        public void TheDeletionConfirmation_StillReadsSensiblyWithoutAnEmail()
        {
            var text = AuthPanelState.DescribeAccountDeletion(null);

            Assert.Contains("this account", text);
            Assert.DoesNotContain("Delete  from", text);
        }

        // ------------------------------------------------------- password validation (item 3)

        [Fact]
        public void PasswordValidation_AcceptsExactlyWhatTheServerAllows()
        {
            // The boundary, from both sides. Nothing else is checked: the server deliberately has
            // no composition rules (NIST 800-63B - length is the control that matters), and a
            // client demanding a symbol would refuse a password the server would have taken.
            Assert.Null(AuthPanelState.ValidateNewPassword(new string('a', 12), new string('a', 12), 12));

            var tooShort = AuthPanelState.ValidateNewPassword(new string('a', 11), new string('a', 11), 12);
            Assert.NotNull(tooShort);
            Assert.Contains("12", tooShort);
        }

        [Fact]
        public void PasswordValidation_AddsNoLengthRuleWhenTheServerStatedNone()
        {
            // MinPasswordLength 0 means "not told". Guessing a minimum would either refuse a
            // password the server accepts or promise an acceptance we cannot deliver.
            Assert.Null(AuthPanelState.ValidateNewPassword("short", "short", 0));
        }

        [Fact]
        public void PasswordValidation_CatchesAMistypedConfirmation()
        {
            Assert.NotNull(AuthPanelState.ValidateNewPassword("correct horse", "correct hose", 12));
            Assert.NotNull(AuthPanelState.ValidateNewPassword(string.Empty, string.Empty, 12));
        }
    }
}
