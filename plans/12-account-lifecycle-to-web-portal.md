# Plan 12: Account lifecycle moves to the server's web portal

> **Rewritten 2026-09-25.** This file replaces the original plan 12, *Invites at registration, and redeeming
> a password reset* (`12-invites-and-password-reset.md`, commit `2c6e1da`, never started), which would have
> added invite tokens and password-reset redemption to this client. The architecture has since changed
> direction: **the client does no account lifecycle at all**, and the server's new web portal
> (`WorkLedger-API/plans/S11-web-portal.md`) does all of it. Two things survive from the original draft, and
> both are credited where they land: the re-vendor discipline (item 1) and the stale lockout-verdict problem
> (item 6), which exists whether a reset is redeemed here or in a browser.

**Tickets:** epic [WL-173](https://mnisan.atlassian.net/browse/WL-173), retitled and its subtasks re-scoped in Jira
on 2026-09-25:

| Subtask | Scope item(s) |
|---|---|
| [WL-174](https://mnisan.atlassian.net/browse/WL-174) | 1, 2: re-vendor at the S11 pin, guard `PortalUrl` |
| [WL-175](https://mnisan.atlassian.net/browse/WL-175) | 3, 4: remove registration, claim and account management |
| [WL-176](https://mnisan.atlassian.net/browse/WL-176) | 5, 6, 7: portal links, revocation copy, the stale lockout verdict, detaching a profile |
| [WL-177](https://mnisan.atlassian.net/browse/WL-177) | 8, 9: tests and docs |

[WL-178](https://mnisan.atlassian.net/browse/WL-178) (a client operator tab) is closed as **won't do**: the
operator surface is the portal's (S11 group D).

**Depends on plans 8–11** (same files: `Sync/ApiSyncProvider.cs`, `Services/SyncCoordinator.cs`,
`ViewModels/AuthPanelState.cs`, `ViewModels/MainWindowViewModel.Sync.cs`, `Views/MainWindow.axaml`) and on the
server's **S11**, whose handoff, **`WorkLedger-API/plans/S11-client-handoff.md`**, is authoritative for the
re-vendor and outranks this plan wherever they disagree. That handoff does not exist until S11 group F lands.

## Context

### The new split

| | Client (this repo) | Server + portal (`WorkLedger-API`) |
|---|---|---|
| Time tracking, local reports, backups | ✔ | |
| Sync: Off / File / Api | ✔ | Api endpoint |
| Sign in to a server with an existing account | ✔ | `POST /auth/login` |
| Sign this install out | ✔ (local only) | |
| Create account, claim a pre-S6 server | | `/portal/setup`, `/portal/register` |
| Change password, reset a forgotten one | | `/portal/account/security`, `/portal/reset` |
| List and remove devices, sign out everywhere | | `/portal/account/*` |
| Delete account | | `/portal/account/delete` |
| Invites, users, admin actions | | `/portal/admin/*` |

The client stays fully useful with no server: nothing in this plan touches Off or File sync, the local
database, profiles for an unsynced install, or reports. And the server no longer needs a client in order to
onboard anyone, because an account can be created and fully managed in a browser before the desktop app is
installed.

### What this plan removes, and why that is safe

Plans 9 and 10 built the client's registration, claim and account-management surface. It is merged and
tested, and this plan deletes most of it. That is deliberate, not a rewrite for its own sake:

- **The server keeps every JSON route those plans call** (S11 item 23, "compatibility routes"), so v0.1.x
  clients in the field keep working and nothing here is a breaking wire change.
- **Much of the subtle code in `ApiSyncProvider` exists only for those calls.** `AdoptFreshToken` (the re-mint
  after `/auth/password` and `/auth/logout-all`), the `credentialGuarded` split of an ambiguous `401`, and the
  local email guard before `DELETE /auth/account`: all three are correct, and all three are complexity the
  client no longer needs once it stops making those calls. Removing them shrinks the surface where a future
  change can re-introduce the WL-142 sign-out bug.
- **What stays is the part that is genuinely the client's:** signing in, Invariant L (at most one unattended
  failed login per credential), the 401/423/429 state machine, the `DatabaseOwner` guard, and per-account
  profiles. None of those change.

### What the client loses sight of

When the client initiated an account change, it could tidy up locally in the same breath. When the change
happens in a browser, the client learns about it only as a bare `401` on its next authenticated request, or
learns nothing at all. Three consequences need handling, and they are items 5–7:

1. **Every portal action that kills tokens looks the same from here.** A password change, a reset, sign out
   everywhere, this device removed, an admin suspension, an account deletion: each is `SessionRevoked`. That
   is already handled correctly (Invariant L: ask for a sign-in, never retry); what changes is that it is now
   the *normal* path for these actions rather than a surprise, so the copy should say so (item 5).
2. **A reset in the browser does not clear this install's lockout verdict.** The server clears its counters
   when a reset is redeemed, but `ApiSyncProvider` holds `LockedOut` on the instance until its `Retry-After`
   lapses, and `EnsureApiProvider` caches that instance by endpoint. So a user who locked themselves out,
   reset their password in the portal and came back to the app would find Sign in **disabled for up to
   `Auth:LockoutMinutes`** against a server that would accept them immediately. The original plan 12 found this
   trap for an in-client reset; moving the reset to the web makes it worse, because the client no longer even
   knows a reset happened. Item 6.
3. **Account deletion no longer releases the `DatabaseOwner` stamp.** WL-143 released it on a successful
   in-client deletion, so the next sign-in to any account could adopt the local data. Deleted in the portal,
   the stamp keeps naming an account that no longer exists, and a later sign-in (even the same person
   re-registering with the same email, who gets a new `UserId`) is routed to a *new* profile, stranding the old
   data. Item 7 gives the user the release WL-143 used to perform.

## Scope

1. **Re-vendor `WorkLedger.Contracts` at S11's pin, as its own commit.** The pin is whatever
   `S11-client-handoff.md` names. **This client never re-vendored at S10's `1e343c8`**, so this one pass picks up
   S10 and S11 together:
   - `AuthCapabilitiesResponse` gains **two** trailing defaulted fields: S10's `RegistrationAcceptsInvite` and
     S11's `string? PortalUrl`.
   - `RegisterUserRequest` gains S10's trailing `InviteToken`. It is irrelevant here (item 3 deletes the
     register call) but arrives with the copy.
   - **Nothing else arrives.** S11 moves `AdminDtos.cs` and `ResetPasswordRequest` out of
     `WorkLedger.Contracts` into the server, so there is no admin file to vendor or decline (the question the
     original plan 12 and `S10-client-handoff.md` had to answer no longer exists).
   - Keep the local conventions: the provenance header per file, explicit `using` directives, and the flat
     layout (`Sync/Contracts/AuthDtos.cs`). `ReportDtos.cs` stays un-vendored.
   - `Sync/` and `Payloads/` must be byte-identical. Confirm with the audit command in the handoff and say so in
     the commit body.
   - **The build must be green on this commit alone**, before any removal. Every new field is trailing and
     defaulted, so it should be; the commit is the proof.
   - Update `Sync/Contracts/README.md`: the pin, what S10 and S11 added, and a new standing note: **records the
     client no longer calls** (`RegisterUserRequest`, `ChangePasswordRequest`, `DeviceSummary`,
     `DeleteAccountRequest`) **are still vendored** because the upstream library still carries them for the
     server's compatibility routes. That is not drift. They leave when upstream removes them at its next
     breaking release.

2. **Guard `PortalUrl`.** In `SyncContractGuardTests`, construct `AuthCapabilitiesResponse` with **all eight**
   fields by name and assert `PortalUrl` and `RegistrationAcceptsInvite` round-trip. The existing six-argument
   constructions keep compiling because both new fields default; pinning them by name is what makes a future
   reorder fail here instead of on the wire. Also assert that a JSON body *without* `portalUrl` (a pre-S11
   server) deserializes to `null`.

3. **Remove registration and claim (plan 9's account-creation half).**
   - `ApiSyncProvider`: `TryRegisterAsync`, `TryClaimAsync`, `CreateAccountAsync`, `DescribeAccountFailure`
     and `AccountOutcome` if nothing else uses it. **Keep `TryGetCapabilitiesAsync` and its caching rules**
     (anonymous, a `404` cached as "sign-in only", a transport failure not cached): capabilities still supply
     the lockout policy for the failure warning, and now the portal URL.
   - `SyncCoordinator`: `RegisterAndSignInAsync`, `ClaimAndSignInAsync`.
   - `AuthPanelState`: `ShowCreateAccount`, `ShowSignupSecret`, `ShowClaimServer`, and the registration side of
     password validation. Keep `IsSignedIn`, `ShowSignIn`, `IsLockedOut`, `StatusMessage`, the disabled-Sign-in
     rule, and the repeated-failure warning, which **still never hardcodes 5 and 15** (they come from
     `MaxFailedLogins`/`LockoutMinutes`).
   - View model and XAML: `ToggleCreateAccount`, `CreateAccountAsync`, `ClaimServerAsync`,
     `SubmitNewAccountAsync`, the new-account form fields (including the never-persisted `SignupSecret`) and
     `ClearNewAccountForm`.
   - **Signing in to an unstamped database still adopts it with no prompt.** That is WL-138's offline-first
     rule and it is untouched: the path from "used the app offline for a week" to "created an account in the
     portal, signed in here" must still carry the local data into the account without a question.

4. **Remove account management (plan 10).**
   - `ApiSyncProvider`: `GetDevicesAsync`, `DeleteDeviceAsync`, `LogoutAllAsync`, `ChangePasswordAsync`,
     `DeleteAccountAsync`, `AdoptFreshToken`, and the `credentialGuarded` parameter on
     `SendAuthenticatedAsync`/`SendForJsonAsync`/`SendForNoContentAsync`. With those five calls gone, **every
     remaining authenticated call is a sync call**, for which a `401` always means the session is dead, so the
     ambiguity `credentialGuarded` resolved no longer exists. Delete the parameter rather than leaving it
     always false.
   - `SyncCoordinator`: `GetDevicesAsync`, `RemoveDeviceAsync`, `SignOutEverywhereAsync`, `ChangePasswordAsync`,
     `DeleteAccountAsync`, and the post-deletion local cleanup that only `DeleteAccountAsync` reached.
     `LocalDbService.ReleaseDatabaseOwnerAsync` **stays**, because item 7 reuses it.
   - View model, XAML and `ViewModels/AccountDeviceViewModel.cs` (+ `AccountDeviceViewModelTests.cs`): the
     devices list, change-password panel, sign-out-everywhere button and danger zone.
   - **`SignOut` is unchanged:** it clears the token and nothing else, and is still an auth event, never a data
     event. It does not call the server. A user who wants the device row gone removes it in the portal.

5. **Portal links and revocation copy.**
   - `AuthPanelState` gains `PortalUrl` (from capabilities, `null` when absent or unreachable).
   - **Signed out, portal known:** under the sign-in row, "No account? Accounts are created on the server's web
     portal." plus an **Open web portal** button. Signed in: **Manage account** (opens the portal) beside Sign
     out.
   - **Signed out, no portal** (`PortalUrl` null: a pre-S11 server, or the portal switched off): "Ask this
     server's administrator for an account." No button. Do not guess a URL from `ApiEndpoint`: a server that has
     not advertised a portal may not have one, and a guessed link that 404s is worse than a sentence.
   - **Opening it:** through Avalonia's launcher (`TopLevel.Launcher.LaunchUriAsync`). **Launch only an
     absolute `https://` URL, or `http://` for a loopback host.** The URL comes from the server, and a
     server-supplied `file:` or custom-scheme URI must not reach the OS shell. Refuse anything else with a
     status message, not an exception.
   - `SessionRevoked` copy: "You've been signed out of this server. This happens when your password is changed
     or reset, when this device is removed, or when you sign out everywhere, including from the web portal.
     Sign in again to resume syncing." Plus the Manage account link when the portal is known.
   - `LockedOut` copy: keep the countdown, and add "If you've forgotten your password, ask an administrator
     for a reset link." When the portal is known, add item 6's button.

6. **The stale lockout verdict after a reset in the browser.** (Carried over from the original plan 12's
   item 6. The fix is the same shape; the trigger is now a button because nothing else can tell the client.)
   - While `AuthState.LockedOut` is showing, offer **"I've reset my password"**. It calls a new
     `SyncCoordinator.ForgetServerVerdict()`, which does what WL-148's `OnProfileChangedAsync` already does and
     calls "the load-bearing line": `_apiProvider = null; _apiProviderEndpoint = string.Empty;`, then
     `ResetSignInFailures()`. The next `EnsureApiProvider` builds a fresh instance with no verdict, no rejected
     credential and no cached capabilities.
   - **It sends nothing.** It only re-enables Sign in. The next request is the user's own sign-in with the
     password they just chose. If the account is in fact still locked, the server answers `423` again (and,
     per S7, writes nothing), and the verdict comes straight back. The per-IP rate limit is protected because
     this costs at most one user-initiated attempt per click.
   - **Invariant L is unaffected:** it bounds *unattended* attempts, and `RunTimerSyncAsync` still skips every
     tick while the state is not `SignedIn`, so dropping the provider cannot start a background login.
   - **Not a profile event (WL-148):** the account and its database are the same; only the credential changed.
     Borrow the provider drop only, not `OnProfileChangingAsync`/`OnProfileChangedAsync` or any of
     `SignInAsync`'s profile machinery.
   - Leave the existing self-lifting verdict alone (`HasLifted` off `Retry-After`). The button is for the case
     where the user knows better than the countdown.

7. **Detach local data from its account.** This is the user-invoked replacement for WL-143's automatic release,
   for accounts deleted (or abandoned) in the portal.
   - In the Settings tab's profile list, on the **active** profile, while **signed out**: **Detach from
     account…** with a confirmation that says what it does: "This keeps every project, task and entry on this
     computer and forgets which server account they belonged to. The next account you sign in with will take
     them over and sync them to its server."
   - Implementation: `LocalDbService.ReleaseDatabaseOwnerAsync()` (the same no-identity row WL-143 and the
     pre-S6 backfill write, which `EvaluateOwnershipAsync` reads as `Unclaimed`), then a new
     `ProfileService.ReleaseActiveProfile()` that clears the active entry's `UserId`/`Email`/`ApiEndpoint` and
     saves the registry. Null `LastSyncedAtUtc` for the profile, as adoption does.
   - **Both halves are required.** `SignInAsync`'s adopt-in-place branch needs the database unclaimed *and* its
     profile belonging to nobody; with only the stamp released, the sign-in would still find a registry entry
     naming the old account and create a new profile. **Do not get the registry half by changing
     `ReconcileActiveProfileAsync`** to clear an entry when the stamp has no identity: it deliberately returns
     early in that case today, it runs at startup and after every switch, and widening it would change what a
     restored unstamped backup does to an account profile. A named method used by exactly one command is the
     smaller change.
   - **Signed out only, active profile only.** Signed in, detaching would contradict the session in hand.
     Detaching a non-active profile would need a database switch, and the user can switch first. Both
     restrictions keep this a one-database, no-switch operation.
   - Not one local row or `SyncQueue` row is touched. The unsent queue is exactly what the next account wants
     pushed, as WL-143 already reasoned.

8. **Tests.** Mostly deletion, and the remaining suites must stay green:
   - **Delete** the tests for removed behaviour: registration/claim/account-management cases in
     `ApiSyncProviderTests`, `SyncCoordinatorTests` and `AuthPanelStateTests`, and
     `AccountDeviceViewModelTests.cs` entirely. List the deleted test names in the PR description so a reviewer
     can check nothing *retained* lost coverage.
   - **Keep and re-run untouched:** every Invariant L test
     (`ManySyncCycles_AgainstAServerThat401sEverything_MakeAtMostOneLoginAttempt` above all), the
     401/423/429 state tests, the WL-138 refusal tests, `ProfileServiceTests`, and all sync, orchestrator and
     file-provider suites.
   - `AuthPanelStateTests`: `PortalUrl` from capabilities, from null capabilities, and from a pre-S11 response;
     the signed-out copy with and without a portal; the "I've reset my password" affordance appears only while
     locked out.
   - Launcher guard: `https://` and loopback `http://` accepted; non-loopback `http://`, `file:`, `javascript:`,
     a custom scheme and a relative URL refused. Test the guard as a pure function; do not launch anything.
   - `SyncCoordinatorTests`: after a scripted `423`, `ForgetServerVerdict()` leaves `Auth.State` not
     `LockedOut` and sends **no request**; a subsequent sign-in with a new credential makes exactly one login
     call; the active profile id is unchanged.
   - Detach: with a stamped database, detach while signed out leaves the stamp `Unclaimed`, the registry entry
     without an account, and every row and queue row count-asserted unchanged; a following sign-in adopts in
     place with no new profile. Detach is refused while signed in.
   - `SyncContractGuardTests`: item 2.

9. **Docs.**
   - `CLAUDE.md`: the vendored-state paragraph (new pin; S10 + S11 inventory; the still-vendored-but-unused
     records), the cross-repo endpoint list (the client now calls only `/auth/login`, `/auth/whoami`,
     `/auth/capabilities`, `/sync/push`, `/sync/pull`), the `ApiSyncProvider` bullets (drop the plan 9/10
     calls, `AdoptFreshToken` and `credentialGuarded`), the `SyncCoordinator` bullets (drop WL-139/WL-143/
     WL-146 account operations, add `ForgetServerVerdict`), the `AuthPanelState` bullet, and a new line under
     Project overview: **the client does no account lifecycle; the server's portal does**. Fix the plans table
     while there: plan 11 is merged, and plan 12 is this one.
   - `plans/README.md`: row 12, its sequencing note, and the dependency on API S11.
   - `plans/09-…` and `plans/10-…`: already carry a banner pointing here. Leave their bodies as the design
     record.
   - `README.md` (user-facing): "Connecting to a server" becomes "get an account from the server's web portal,
     then sign in here".

## Out of scope

- **Any account operation in the client**, including the ones the original plan 12 proposed (an invite field,
  a reset panel). They are portal pages now (S11 items 17–21).
- **An operator surface in the client** (WL-178, won't do).
- **Deleting a profile's data from the client**, or detaching a non-active profile. Worth a follow-up if the
  profile list grows stale entries; not needed to replace WL-143.
- **An embedded browser for the portal.** The system browser is where password managers and existing sessions
  live.
- **Remember-me / OS credential store.** Unchanged from plan 8: Invariant L still permits it later.
- **Anything under `Sync/`** beyond the vendored copy, and anything in `SyncOrchestrator`. The wire format is
  unchanged.
- **Removing the still-vendored unused records.** They follow upstream.
- [WL-68](https://mnisan.atlassian.net/browse/WL-68) (`TaskItemViewModel` abstraction): still open, untouched.

## Acceptance criteria

- `Sync/Contracts/` is verbatim upstream at the S11 pin (bar provenance headers and `using` directives), with
  no admin DTOs and no `ResetPasswordRequest`; `dotnet build` is green on the re-vendor commit alone.
- The client offers no way to register, claim, change or reset a password, list or remove devices, sign out
  everywhere or delete an account. `ApiSyncProvider` calls only `/auth/login`, `/auth/whoami`,
  `/auth/capabilities`, `/sync/push` and `/sync/pull`.
- Against an S11 server with the portal on, the signed-out panel offers Open web portal and the signed-in panel
  offers Manage account; both open the advertised URL in the system browser. Against a pre-S11 server or one
  with the portal off, the panel shows the administrator sentence and no button.
- A server-supplied portal URL that is not `https://` (or loopback `http://`) is never launched.
- A password change, reset, device removal, sign-out-everywhere or account deletion done in the portal
  surfaces here as a sign-in prompt with copy that names those causes, and never as a retry loop.
- After locking out and resetting in the portal, "I've reset my password" re-enables Sign in immediately, sends
  nothing by itself, and the new password signs in on the first attempt.
- After an account is deleted in the portal, Detach from account (signed out) lets the next sign-in adopt the
  local data in place, with not one row changed.
- Off and File sync, profiles, backups, reports and the WL-138 guard behave exactly as before.
- `dotnet test` green, with every Invariant L and WL-138 test unmodified.

## Suggested commit messages

Two commits on one branch, so the pin bump is attributable on its own:

```
chore(contracts): re-vendor WorkLedger.Contracts at <S11 pin> (WL-174)

feat(auth)!: move account lifecycle to the server's web portal (WL-173)
```

The second is `!` because users lose in-app registration and account management; the wire is unchanged.

## Prompt for Claude Code

> Read `WorkLedger-API/plans/S11-client-handoff.md` first; it is authoritative for the re-vendor. Then implement
> `plans/12-account-lifecycle-to-web-portal.md`.
>
> The client is being reduced to time tracking plus signing in. Every account operation (registration, claim,
> password change and reset, device management, sign out everywhere, account deletion) now happens in the
> server's web portal. Land the re-vendor as its own green commit, then remove plans 9 and 10's account
> surface from `ApiSyncProvider`, `SyncCoordinator`, `AuthPanelState`, the view model and the XAML, including
> `AdoptFreshToken` and the `credentialGuarded` parameter, which exist only for the removed calls.
>
> Do **not** touch sign-in, Invariant L, the 401/423/429 states, the WL-138 ownership guard, profile switching,
> or anything under `Sync/` beyond the vendored copy. Their tests must pass unmodified.
>
> Add portal links from `AuthCapabilitiesResponse.PortalUrl`, launching only `https://` (or loopback `http://`)
> URLs. Add "I've reset my password", which drops the cached `ApiSyncProvider` exactly as
> `OnProfileChangedAsync` does and sends nothing. Add "Detach from account" for the active profile while
> signed out, via `ReleaseDatabaseOwnerAsync` plus a new `ProfileService.ReleaseActiveProfile()` (both halves,
> see item 7). Neither is a profile switch.
