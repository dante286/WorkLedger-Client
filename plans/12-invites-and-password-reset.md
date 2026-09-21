# Plan 12 — Invites at registration, and redeeming a password reset

**Tickets:** epic [WL-173](https://mnisan.atlassian.net/browse/WL-173). The server side is
[WL-150](https://mnisan.atlassian.net/browse/WL-150) / `WorkLedger-API/plans/S10-multi-user-operations.md`,
handed off in **`WorkLedger-API/plans/S10-client-handoff.md`**, which is authoritative for this cutover and
outranks anything below it where the two disagree.

| Subtask | Scope item(s) |
|---|---|
| [WL-174](https://mnisan.atlassian.net/browse/WL-174) | 1, 2 — re-vendor at `1e343c8`, and guard the two records that grew |
| [WL-175](https://mnisan.atlassian.net/browse/WL-175) | 3, 4, 5 — an invite token as an alternative registration credential |
| [WL-176](https://mnisan.atlassian.net/browse/WL-176) | 6, 7, 8 — redeem a password reset from the signed-out state |
| [WL-177](https://mnisan.atlassian.net/browse/WL-177) | 9, 10 — tests and docs |

**Depends on Plans 8, 9, 10 and 11** — same files as all four (`Sync/ApiSyncProvider.cs`,
`Services/SyncCoordinator.cs`, `ViewModels/AuthPanelState.cs`, `ViewModels/MainWindowViewModel.Sync.cs`,
`Views/MainWindow.axaml`), and on the server's **S10**, which has merged.

**Plan 11 (per-account profiles, WL-148) landed first and changes two things here.** It reworked
`SignInAsync` around profile switching and added `IProfileAware`, so items 6–8 must be read against that
code rather than against plan 10's. It also supplies the precedent item 6 follows: `OnProfileChangedAsync`
already drops the cached `ApiSyncProvider`, and its own remark calls that "the load-bearing line" for exactly
the reason a reset needs it.

## Context

**This one is cheap, and the handoff says so first because it is the least believable part.** S9 was a
cutover that deleted types and broke every call site on purpose. S10 is additive: the two records that grew
took their new field last and with a default, nothing deconstructs either positionally, and **the client
compiles unchanged after the re-vendor**. The handoff checked that call site by call site rather than
assuming it. Re-vendoring is therefore a copy, a pin bump and a green build, and everything after it is a
choice.

| | Before | After |
|---|---|---|
| Vendored pin | `3d27c86` (2026-09-15, WL-133) | **`1e343c8`** — the last commit to touch `src/WorkLedger.Contracts/` |
| `RegisterUserRequest` | `(Email, Password, DisplayName)` | `+ string? InviteToken = null` |
| `AuthCapabilitiesResponse` | six fields | `+ bool RegistrationAcceptsInvite = false` |
| New file | — | `Auth/AdminDtos.cs` — six dependency-free records |
| `Sync/`, `Payloads/` | | **zero bytes changed** since the current pin |

That last row is not "no behavioural change" — `git diff 3d27c86..HEAD -- src/WorkLedger.Contracts/Sync
src/WorkLedger.Contracts/Payloads src/WorkLedger.Contracts/Reports` is empty, verified against the API
working copy before this plan was written. The merge rule, `SyncJson.Options`' PascalCase policy, the
positional `SyncPushResponse.Results` and `SyncItemResult.AuthoritativeOperation` are all untouched, so
**nothing in this plan goes near `SyncOrchestrator`**. Re-verify with that command before trusting this
paragraph; it is the one claim a future S11 could silently invalidate.

Three things are worth reading twice.

1. **`registrationRequiresSecret` stopped lying.** It was hardcoded `true` before S10 and now reports
   `Auth:SignupSecretEnabled`. This is **the only behavioural change to an endpoint the client already
   calls**, and it needs no code change: `AuthPanelState.ShowSignupSecret` already projects the field, so a
   server that has moved to invite-only will correctly stop offering a field that could only earn a `401`.
   What it does mean is that the secret can no longer be assumed — see item 4.

2. **The two flags are not a mode switch.** Both true means both credentials are accepted and a client may
   offer either path. A server running S10 always sets `RegistrationAcceptsInvite: true` (it is not
   configurable — it exists so a client can tell this server from an older one without version-sniffing), so
   an *absent* field deserializing to `false` is correctly read as "this server predates invites".

3. **A password reset signs every device out, including this one, and the server clears its lockout
   counters.** The first half is on the wire (`204`, no token, because tokens are `(user, device)`-scoped and
   this call names no device). The second half is the trap: this install's lockout verdict lives on the
   `ApiSyncProvider` *instance*, `EnsureApiProvider` caches that instance by endpoint, and `SignOut()`
   deliberately does not rebuild it. So a user who locked themselves out, obtained a reset token and
   successfully spent it would face a **disabled Sign in button for up to fifteen more minutes**, against a
   server that would have accepted them immediately. Item 6 is where that is prevented.

### What changed on the wire

| Endpoint | Change |
|---|---|
| `GET /auth/capabilities` | `+ registrationAcceptsInvite`; `registrationRequiresSecret` now reports the real setting |
| `POST /auth/register` | body may carry `inviteToken`; gate is a valid `X-Bootstrap-Secret` **or** a valid unredeemed, unexpired invite |
| `POST /auth/password/reset` | **new.** Anonymous, rate-limited with the other credential endpoints. `ResetPasswordRequest(ResetToken, NewPassword)` → `204`, **no token** |
| `POST /auth/claim` | unchanged — it ignores `inviteToken`, and still ignores `SignupSecretEnabled` so an operator can always take ownership of pre-existing data |
| `/admin/*` | twelve new routes, all behind an `admin` policy. The client needs **none** of them. Out of scope; see below |

Registration's status codes, which item 3 has to tell apart: `403` registration closed outright (invites
included), `401` neither credential accepted, `400` bad email or password, `409` **email taken *or* the
invite is expired, already spent, or addressed to somebody else**. The status does not distinguish that last
pair; the server's message does, on purpose — the caller already holds a one-time token addressed to them,
so there is nothing to hide, and a vague `401` would send them back to whoever issued it.

## Scope

1. **Re-vendor `WorkLedger.Contracts` at `1e343c8`, as its own commit.** Copy
   `WorkLedger-API/src/WorkLedger.Contracts/` over `Sync/Contracts/`, keeping this repo's two local
   conventions — the provenance header on each file and the explicit `using` directives, since this project
   does not enable `ImplicitUsings`. Note the vendored folder is **flat**: upstream's `Auth/AuthDtos.cs` is
   `Sync/Contracts/AuthDtos.cs` here, so the new file lands as `Sync/Contracts/AdminDtos.cs`.
   - **Vendor `AdminDtos.cs` whole, and build nothing against it.** Six dependency-free records cost
     nothing, and a half-vendored file is the one outcome the handoff rules out. Record the decision in
     `Sync/Contracts/README.md` alongside the standing one about `ReportDtos.cs`, which **stays
     un-vendored** — that is not drift.
   - Bump the pin in `Sync/Contracts/README.md` from `3d27c86` to `1e343c8` and note that server `main` has
     moved past it (docs and the rate limiter touched no contract file), which is why the Contracts commit is
     pinned rather than `main` — exactly as WL-133 did.
   - Copy `Sync/` and `Payloads/` anyway so the folder stays a faithful copy, but nothing in them needs
     reading. Confirm the no-op with the diff command above and say so in the commit body.

2. **Guard the two records that grew.** In `Tests/WorkLedger.Tests/SyncContractGuardTests.cs`:
   - Extend `AuthCapabilitiesResponse_CarriesTheLockoutPolicyAsWellAsRegistration` — or add a sibling — to
     construct **all seven** fields by name and assert `RegistrationAcceptsInvite` survives the round trip.
     The existing six-named-argument construction keeps compiling because the seventh defaults; pinning it
     explicitly is what makes a future reorder fail here rather than on the wire.
   - Add a `RegisterUserRequest` test asserting the fourth parameter exists, defaults to `null` when
     unsupplied, and round-trips when set. A server predating S10 ignores the extra property, which is why
     sending it unconditionally would be safe — and why the client still should not (item 3).

3. **`ApiSyncProvider`: carry an invite token on register, and only on register.**
   - `TryRegisterAsync` gains a fifth parameter, `string? inviteToken`; `CreateAccountAsync` passes it into
     `new RegisterUserRequest(email, password, displayName, inviteToken)`.
   - **`TryClaimAsync` passes `null` and must keep doing so.** `/auth/claim` ignores the field, and sending
     one would imply to the next reader that claiming can be invite-gated. The comment above
     `CreateAccountAsync` currently says register and claim "take the same three inputs and the same secret
     header"; that stops being true, so rewrite it rather than leaving it to rot.
   - `DescribeAccountFailure` needs three edits. `401` currently reads *"That signup secret wasn't
     accepted"* — wrong, and actively misleading, for a user who typed an invite token instead; it must name
     whichever credential(s) were offered. `409` for registration must try `ExtractServerError` **first**,
     because the server's message is the only thing that distinguishes a taken email from a dead invite, and
     fall back to the current wording. `403` should say registration is closed outright, invites included.
   - `CapabilitiesStale` on `403`/`409` keeps its current meaning and is now more useful: an invite spent
     between the probe and the submit is exactly a server that has moved on.

4. **`AuthPanelState`: `ShowInviteToken`, and "at least one credential".**
   - `ShowInviteToken { get; private init; }`, projected from `capabilities?.RegistrationAcceptsInvite ??
     false`. Null capabilities and an absent field both mean "no invites here", which degrades to the
     pre-S10 UI rather than to an error.
   - Both flags may be true; render **both** fields and require **either**. Add
     `ValidateRegistrationCredential(secret, inviteToken, showSecret, showInvite)` returning a message when
     the server accepts a credential and the user supplied none. This is a local refusal on purpose: the
     alternative is spending a `401` against a limiter that is **per-IP and shared with `/auth/login`**, so a
     few empty submits would rate-limit sign-in for every device on that network. Same reasoning as the
     disabled Sign in button during a lockout.
   - When only the invite is accepted, the message must ask for a token and not mention a secret the server
     will refuse.

5. **View model and XAML: one optional field on the create-account form.**
   - `CanUseInviteToken` (from `ShowInviteToken`, set in `ApplyAuthPanelState`) and an `InviteToken`
     observable property. **Never persisted** — no autosave hook and no `AppSettings` field, the same
     treatment `SignupSecret` gets — cleared in `ClearNewAccountForm` and lifted out of the bound property
     before the await, alongside the password.
   - `SubmitNewAccountAsync` runs the new credential validation before anything else, passes the token on
     the register path and `null` on the claim path.
   - XAML: a row bound to `CanUseInviteToken`, between Confirm and Signup secret. **Not** a `PasswordChar`
     field: this is a token pasted out of a chat window, and a user who cannot see what they pasted cannot
     tell a truncated token from a wrong one.

6. **`ApiSyncProvider.ResetPasswordAsync(resetToken, newPassword)` — anonymous, and it clears the verdict.**
   - Send with `SendOnceAsync(..., authorize: false)`. **Not** `SendForNoContentAsync`: that attaches a
     bearer and applies plan 8's 401 policy, and this endpoint is anonymous and is called precisely when
     there may be no session at all. Attaching a dead token to it could only produce noise.
   - Returns `AccountActionOutcome` — this is the fifth act with the same two outcomes, which is what that
     type exists for.
   - Failure mapping: `400` is the server's own message via `ExtractServerError` (an unknown, expired or
     already-spent token all answer the **same** `400` with the same message, deliberately — do not try to
     tell them apart), and where the message is about password length the copy should add that **the token
     is still spendable**, so the user retries with a longer password rather than asking for another token.
     `409` means a pre-S6 credential-less row and should point at Claim. `429` gets the existing
     `Retry-After` wording.
   - **The stale-verdict problem, and the shape the fix should take.** The server has just cleared its
     lockout counters and invalidated the old password, but this install's `LockedOut` verdict lives on the
     `ApiSyncProvider` instance and will not lapse until the original window elapses — so Sign in stays
     disabled against a server that would accept the new password immediately. `EnsureApiProvider` caches
     that instance by endpoint and `SignOut()` does not rebuild it, so nothing else clears it.
     - **Prefer dropping the cached provider in the coordinator** (item 7), the way WL-148's
       `OnProfileChangedAsync` already does — `_apiProvider = null; _apiProviderEndpoint = string.Empty;`.
       Its remark names dropping the provider as "the load-bearing line" and lists the same three things
       that go stale: the 401 verdict, the capability cache and the rejected credential. Following an
       existing precedent beats adding a second, narrower way to reach the same state.
     - Mutating `_verdict`/`_rejectedCredential` from inside `ResetPasswordAsync` is the alternative. It
       keeps the capability cache, which is still valid because the *server* has not changed — but it puts
       session-lifecycle logic in a method that is otherwise a plain HTTP call, and it leaves two mechanisms
       to keep in step. Take it only if the re-probe turns out to matter.

7. **`SyncCoordinator.ResetPasswordAsync` — never throws, and touches nothing it does not have to.**
   - On success: `SignOut()`, because the server killed every token including this one and what is held is
     now a dead credential — and `SignOut()` additionally rebuilds the orchestrator, so no timer keeps
     grinding with it. Then drop the cached provider (item 6) and `ResetSignInFailures()`, because the
     server reset its own counters and a stale local count would warn about a threshold nowhere near.
   - **Explicitly untouched:** `ApiEmail` (it prefills the form the user is about to use), the
     `DatabaseOwner` stamp, `LastSyncedAtUtc`, `SyncMode`, `SyncQueue`, and every local row. A reset is a
     credential change, not an account change — the opposite of WL-143's deletion, and worth stating in the
     same explicit register so the difference is deliberate rather than incidental.
   - **And explicitly not a profile event (WL-148).** The active `Profile` must not switch, be created, or
     be re-adopted: the account is the same account, and only its password changed. Do **not** route this
     through `SignInAsync`'s profile machinery or call `AdoptActiveProfile` — resetting a password is not a
     statement about which account this database belongs to. `OnProfileChangingAsync`/`OnProfileChangedAsync`
     are for a database swap, which this is not; borrow only the provider drop, not the hooks.
   - On any failure nothing changes at all.

8. **View model and XAML: the "I have a reset token" panel.**
   - Collapsed by default, in the signed-out area beside Create an account, with a `ToggleResetPassword`
     command mirroring `ToggleCreateAccount`. Fields: reset token (visible text, as in item 5), new
     password, confirm.
   - **Not gated on capabilities.** The endpoint has no flag to advertise it, and a server predating S10
     answers `404`, which surfaces as a legible failure rather than a hidden feature. Do gate it on being
     signed *out* — a signed-in user wants `POST /auth/password`, which plan 10 already built.
   - Validate with the existing `AuthPanelState.ValidateNewPassword` against `MinPasswordLength`; no second
     password rule anywhere.
   - On success, close the panel, clear all three fields, and toast that **every device has been signed out
     and needs to sign in again** — the one consequence a user will not predict. The sign-in row is already
     prefilled with `ApiEmail`, which is the next thing they need.

9. **Tests.** No new frameworks; extend what is there.
   - `ApiSyncProviderTests` (`RecordingHandler`): register carries `InviteToken` in the body when given and
     serializes it as null when not; **claim never carries one**; a `409` on register surfaces the server's
     message rather than the built-in "email already exists"; a `401` message does not name the secret when
     only an invite was sent.
   - Reset: the request carries **no `Authorization` header**; a `204` clears a `LockedOut` verdict so
     `Auth.State` is no longer `LockedOut` immediately afterwards (the regression this plan exists to
     prevent); a `400` surfaces the server's message; a failure leaves the token store alone.
   - `SyncCoordinatorTests`: a successful reset clears token and expiry, resets the failure count, and leaves
     `ApiEmail`, `SyncMode`, `LastSyncedAtUtc`, the `DatabaseOwner` stamp and **every local row and
     `SyncQueue` row** untouched — count-asserted, the way WL-143's deletion test does it. A failed reset
     changes nothing. **And the active profile is the same profile afterwards** (WL-148): same id, no new
     registry entry — the coordinator takes a `ProfileService?` that is null in most tests, so this one
     needs a real one, as `ProfileServiceTests` sets up.
   - `AuthPanelStateTests`: `ShowInviteToken` from the flag, from a null capabilities, and from a pre-S10
     six-field response; both flags true renders both; the credential validator in each of its three cases.
   - `SyncContractGuardTests`: item 2.

10. **Docs.** `CLAUDE.md` — the vendored-state paragraph (pin → `1e343c8`, and the S10 inventory), the
    `ApiSyncProvider` and `SyncCoordinator` bullet lists, and the plans table gaining row 11.
    `Sync/Contracts/README.md` — the pin, the `AdminDtos.cs` decision, and a line on what S10 added.
    `plans/README.md` — the row, the ticket links and a sequencing note. `README.md` only if it describes the
    sign-in panel's fields.

## Out of scope

- **The twelve `/admin/*` routes and any operator UI.** Deliberate, and the DTOs are vendored so it stays
  possible rather than because it is expected. The server's own README documents the operator flow as
  `curl`, which is a legitimate permanent answer for a self-hosted box; a client admin surface — issuing and
  revoking invites, listing users, minting reset tokens, promote/demote/disable — is a real feature and its
  own decision. Filed as [WL-178](https://mnisan.atlassian.net/browse/WL-178), outside this epic.
- **Self-service "forgot password".** It needs SMTP the server does not have; S10 names it in its own Out of
  scope so the gap is on the record. Until then a reset token arrives out of band from an operator, which is
  exactly what item 8 assumes.
- **Anything in `Sync/`** beyond the vendored copy. The wire format did not change.
- **Per-account profiles ([WL-148](https://mnisan.atlassian.net/browse/WL-148))** — **merged** as plan 11,
  and a dependency of this one rather than a peer. Nothing here extends it; item 7 only states what a reset
  must not do to it.
- The `TaskItemViewModel` abstraction ([WL-68](https://mnisan.atlassian.net/browse/WL-68)) — still open,
  untouched by S10.

## Acceptance criteria

- `Sync/Contracts/` is verbatim upstream `1e343c8` bar the provenance headers and `using` directives;
  `AdminDtos.cs` is present and whole; `ReportDtos.cs` is still absent and still explained.
- `dotnet build` is green **on the re-vendor commit alone**, before any other change — that is the claim the
  handoff makes and the commit is what proves it.
- Registering against a server with `Auth__SignupSecretEnabled=false` succeeds with an invite token and no
  secret, and the secret field is not shown.
- Registering with both flags true succeeds by either credential, and submitting neither is refused
  locally, with no request sent.
- A spent or expired invite shows the server's own sentence, not "An account with that email already
  exists".
- Against a pre-S10 server (no `registrationAcceptsInvite`, secret hardcoded) the panel is exactly what it
  is today.
- Redeeming a reset token from a **locked-out** state signs the user out, and the Sign in button is
  immediately enabled and accepts the new password — no countdown left over.
- After a reset: not one local row, `SyncQueue` row, settings field beyond token/expiry, or the
  `DatabaseOwner` stamp has changed.
- `dotnet test` green, including the new cases.

## Suggested commit message

Two commits on one branch, so the pin bump is attributable on its own:

```
chore(contracts): re-vendor WorkLedger.Contracts at 1e343c8 (WL-174)

feat(auth): accept an invite token at registration and redeem password resets (WL-173)
```

## Prompt for Claude Code

> Read `WorkLedger-API/plans/S10-client-handoff.md` first — it is authoritative for this cutover. Then
> implement `plans/12-invites-and-password-reset.md`.
>
> Land the re-vendor as its own commit and prove the build is green on it before touching anything else;
> the handoff's central claim is that the client compiles unchanged, and the commit is the evidence.
>
> Two things in this plan are not obvious and are the reason it exists. First, a `409` from `/auth/register`
> now means *either* a taken email *or* a dead invite, and only the server's message distinguishes them — so
> show it. Second, `POST /auth/password/reset` clears the server's lockout counters, while this install's
> lockout verdict lives on the cached `ApiSyncProvider` instance that `SignOut()` does not rebuild — so drop
> that cached provider after a successful reset, exactly as WL-148's `OnProfileChangedAsync` already does,
> or a user who just fixed their password still cannot sign in.
>
> Read `plans/11-per-account-profiles.md` before items 6–8: WL-148 reworked `SignInAsync` around profile
> switching, and a password reset must borrow its provider drop without becoming a profile event.
>
> Do not touch `SyncOrchestrator` or anything under `Sync/` beyond the vendored copy: the wire format is
> byte-identical since the current pin, verified. Do not build an admin UI — the DTOs are vendored so it is
> possible, not so it is expected.
