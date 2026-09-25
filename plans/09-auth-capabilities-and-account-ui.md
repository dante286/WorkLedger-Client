# Plan 9 — Registration, claim, and a capability-aware auth UI

> **Partly superseded (2026-09-25).** Registration and claim (the account-creation half of this plan) are
> removed from the client by
> [plan 12](12-account-lifecycle-to-web-portal.md) and now live in the server's web portal
> (`WorkLedger-API/plans/S11-web-portal.md`). The capability probe, the sign-in states and the
> lockout-aware panel stay. This file remains the design record of what shipped.

**Ticket:** [WL-146](https://mnisan.atlassian.net/browse/WL-146), the second subtask of
[WL-136](https://mnisan.atlassian.net/browse/WL-136), under epic
[WL-105](https://mnisan.atlassian.net/browse/WL-105). Plan 8 lands WL-136's other subtask
([WL-145](https://mnisan.atlassian.net/browse/WL-145)) — the plain sign-in row; this plan lands the part the
server built `GET /auth/capabilities` *for*.

**Depends on Plan 8** (the re-vendored contract and the login path). Same file surface —
`MainWindowViewModel.Sync.cs`, `Views/MainWindow.axaml` — so it cannot run in parallel with Plan 8 or Plan 10.

## Context

The server exposes `GET /auth/capabilities` (anonymous) →
`AuthCapabilitiesResponse(bool RegistrationEnabled, bool RegistrationRequiresSecret, int MinPasswordLength, bool HasUnclaimedAccount)`
for exactly one reason, stated in S6 scope item 8: **so this client can hide its registration UI when the
server has registration switched off.** A client that only learns the flag from a `403` has already shown the
user a form that cannot work. Each of the four fields answers a specific question the UI otherwise has to guess:

| Field | What the UI does with it |
|---|---|
| `RegistrationEnabled` | Show the *Create account* affordance at all. |
| `RegistrationRequiresSecret` | Show the bootstrap-secret field on the registration form. Plan 8 removed the secret from the sync configuration; this is the only place it comes back, and only for the duration of one call. |
| `MinPasswordLength` | Client-side length validation that **matches** the server (default 12) instead of guessing. |
| `HasUnclaimedAccount` | Offer *Claim this server* instead of *Create account* — the upgrade path for a deployment that already holds data from before user accounts existed. |

The claim path is worth understanding before building the button. A pre-S6 deployment migrates in place: its
one seeded tenant keeps every project, task, entry and device, and gains a null `PasswordHash` marking it
*unclaimed*. `POST /auth/claim` attaches an email and password to **that row**, so the existing data carries
over. It cannot be logged into first — login refuses a null hash — and once claimed the endpoint answers `409`
forever. So "Claim this server" is a one-shot, and the person who needs it is exactly the person whose data is
already on the box.

Two server behaviours the UI must not smooth over:

- **Registration and login disagree on honesty, deliberately.** `POST /auth/register` answers a duplicate email
  with `409 An account with that email already exists.` — accepted account enumeration, behind a shared secret,
  on a self-hosted box. `POST /auth/login` answers unknown email, wrong password, and unclaimed-account with an
  **identical** `401 Invalid email or password.` Do not "improve" that 401 into "no account with that email";
  the server went out of its way to make those indistinguishable and the client is the last place that
  distinction could leak.
- **`Auth:RegistrationEnabled = false` does not affect login.** A closed server still lets its existing users
  in. Gating the sign-in form on the registration flag would lock every user out of a server whose owner shut
  the door behind them.

## Scope

1. **Capabilities probe on `ApiSyncProvider`.** `TryGetCapabilitiesAsync()` → `GET auth/capabilities`, sent
   **without** a bearer token (it is anonymous, and a stale token must not turn feature discovery into a 401).
   Total, like everything else on this provider: unreachable, non-2xx and malformed all come back as null
   rather than throwing.
   - **Degrade when the field or endpoint is missing.** A `404` means a server predating S6 — treat it as
     "no registration UI, sign-in only" rather than an error, and say so in a comment. This is the same class of
     forward-compatibility the `AuthoritativeOperation` fallback handles on the sync side.
   - Cache the result per endpoint for the app run. It is checked whenever the auth panel is shown and it does
     not change under a running server; re-probe when the endpoint setting changes or the user asks.

2. **Render Plan 8's `AuthState`.** Plan 8 item 4a defines it (`SignedOut`, `SignedIn`, `SessionExpired`,
   `SessionRevoked`, `LockedOut(until)`); this plan is where it becomes visible. One property the UI switches
   on, not four booleans that can disagree. Everything below is a projection of it plus the cached capabilities.

   Three of the five states have never been on screen before and each needs its own copy:

   - `SessionExpired` — "Your session expired on *date*. Sign in to resume syncing." Reached locally with no
     request, so it can be shown the moment the panel opens.
   - `SessionRevoked` — item 6 below.
   - `LockedOut(until)` — **disable the Sign in button until that instant and show the countdown.** The server
     refuses the correct password while locked, so an enabled button can only produce another 423 and eat the
     per-IP rate limit that every other device behind the same address shares. This is the one place the UI is
     load-bearing for the lockout design rather than merely reporting it.

   **Persistent state, not a toast stream.** Plan 8 item 4e requires the toast to fire on the *transition* into
   a signed-out state; the state itself lives in the panel until resolved. A background tick every 15 minutes
   that toasted each time would produce ~96 notifications a day and get the feature switched off.

2a. **Warn before the last attempts, without automating** (Plan 8 item 4h). Count consecutive failed
   interactive sign-ins for the current email in memory, and from the third, warn *before* sending: name the
   real threshold and lockout duration when `AuthCapabilitiesResponse` reports `MaxFailedLogins` /
   `LockoutMinutes` (WL-147), otherwise stay generic — never hardcode 5 and 15, an operator may have changed
   them. Reset on success, on an email change, and on restart.

   Warn at three rather than at the threshold because `FailedLoginCount` is per **user**, not per device:
   attempts from another machine spend the same budget, so this client's count is a lower bound on the server's
   and never the truth. Say that in a comment — it is the reason the number looks conservative.

3. **Registration form**, visible only when `RegistrationEnabled` and nothing is signed in. Email, password,
   confirm, optional display name, and the bootstrap secret **only** when `RegistrationRequiresSecret`. On
   success (`201` + `UserSummary`) the server returns **no token** — registration is a user-level act with no
   device bound to it — so the client must follow with a login to get one. Do that automatically using the
   credentials just typed; making the user type them twice in a row is a worse experience than one extra round
   trip.
   - Validate length client-side against `MinPasswordLength`, and **never more strictly than the server
     reports**. No composition rules: the server deliberately has none (NIST 800-63B — length is the control
     that matters), and a client that demands a symbol makes a password the server would have accepted
     un-typeable.
   - Map the distinct failures to distinct messages: `403` registration disabled (and re-probe capabilities —
     the operator flipped it since the probe), `401` wrong secret, `400` validation, `409` duplicate email,
     `429` rate-limited. Note `/auth/register` and `/auth/claim` share the per-IP rate limiter with
     `/auth/login`, so a `429` here may have been earned by sign-in attempts — word it about the address, not
     about registration.

4. **"Claim this server"**, visible only when `HasUnclaimedAccount`. Same body as registration, hitting
   `POST /auth/claim`, and **not** gated on `RegistrationEnabled` — an operator who closed registration must
   still be able to claim their own existing data. Word the button around what it actually does: it takes
   ownership of the data already on the server, it is not a way to make a second account. Follow with a login,
   as above. `409 Nothing to claim.` means someone else got there first or the probe was stale — re-probe and
   say so.

5. **Signed-in identity.** Show the email and display name from `LoginResponse.User`, refreshed from
   `GET /auth/whoami` when the panel opens (it is cheap and it doubles as a liveness check that the token is
   still good). Not just cosmetic: on a multi-user server "which account am I pushing into?" is the question
   behind the data-integrity guard Plan 8 item 6 exists for.

6. **Route a revoked session to the form.** This is the behavioural note the epic leads with. When a sync comes
   back `AuthenticationRequired`, the app must land the user in the sign-in form with the email prefilled and a
   message saying the session ended — not a red toast reading `Sync failed: 401` that a user can only respond to
   by pressing *Sync now* again. Name the plausible causes without pretending to know which: the password was
   changed elsewhere, this device was removed from another machine, or the account was deleted.

7. **Sign out**, and make it honest about scope. It clears this device's token locally; it does not revoke it
   server-side (that is `/auth/logout-all`, Plan 10) and it does not touch local data. Say both in the
   confirmation, because "sign out" reads like "disconnect and forget" and this app's local database is the
   user's actual work.

8. **Tests.**
   - Capabilities: parsed correctly; a `404` degrades to sign-in-only rather than failing; an unreachable
     endpoint degrades the same way; the probe carries no `Authorization` header even when a token is stored;
     absent `MaxFailedLogins`/`LockoutMinutes` fall back to generic warning copy rather than to hardcoded
     numbers.
   - `LockedOut` disables the Sign in command until the `Retry-After` instant and re-enables it after — the
     lockout half of Plan 8's design, and the only part of it that lives in the UI.
   - Each of `SessionExpired`, `SessionRevoked` and `LockedOut` renders its own message, and none of them is
     the generic sync-failure toast.
   - Repeated failed sign-ins produce the pre-send warning from the third attempt, and the counter resets on a
     success.
   - Registration UI state: hidden when `RegistrationEnabled = false`, secret field present only when
     `RegistrationRequiresSecret`, claim offered only when `HasUnclaimedAccount`, and **the sign-in form
     available in every combination** — including `RegistrationEnabled = false`.
   - Register → auto-login stores a token; a `409` leaves nothing signed in and re-probes capabilities.
   - Client-side validation accepts exactly what `MinPasswordLength` allows — one test at the boundary, one
     below it.
   - The login failure message is the same for unknown email, wrong password and unclaimed account (assert the
     view model produces one string, so nobody later derives three).
   - An `AuthenticationRequired` sync result moves the view model into the signed-out-by-server state with the
     email retained.

## Out of scope

- **`/auth/password`, `/auth/devices`, `/auth/logout-all`, `DELETE /auth/account`** — Plan 10.
- **Password reset and email verification.** Neither exists server-side; both need SMTP the deployment doesn't
  have (S6/S7 Out of scope). If a user forgets their password the recovery path is a `psql` `UPDATE` on the
  server, documented in the server's README — do not build a UI implying otherwise.
- **Changing the account email or display name.** Not exposed server-side (S8 Out of scope).
- **Storing the password**, still. Auto-login after registration uses what is in the form field for the
  duration of the call.
- **Multiple accounts on one install.** The local database has no user column; Plan 8's guard is the whole
  answer for now.
- **`SyncOrchestrator`, the merge rule, and the sync wire format.** Untouched.

## Acceptance criteria

- With `Auth:RegistrationEnabled = false` the client shows no registration affordance at all, and existing
  users can still sign in.
- With registration enabled, a new account can be created and is signed in immediately afterwards without
  re-typing credentials; the bootstrap-secret field appears exactly when the server says it is required.
- Against a server with an unclaimed pre-user-accounts account, "Claim this server" takes ownership and the data
  already on the server is visible to this device afterwards.
- Client-side password validation matches `MinPasswordLength` and adds no rules of its own.
- Unknown email, wrong password and unclaimed account are indistinguishable in the UI.
- A server predating `/auth/capabilities`, or an unreachable one, leaves a usable sign-in-only UI rather than an
  error state.
- A revoked session lands the user in the sign-in form with their email prefilled and an explanation, not in a
  generic sync-failure toast — shown as persistent panel state, toasted once on the transition, not once per
  sync tick.
- While the account is locked out, Sign in is disabled with a visible countdown, so the UI cannot spend
  attempts the server will refuse anyway or burn the shared per-IP rate limit.
- The repeated-failure warning names the server's real threshold and duration when it reports them, and is
  generic when it does not — no hardcoded 5 or 15.
- Full suite green, no real network calls.

## Suggested commit message

```
feat(auth): add account registration, server claim, and a capability-aware sign-in UI
```

## Prompt for Claude Code

> Plan 8 must be merged first. Read `plans/08-user-accounts-login-cutover.md`, then
> `WorkLedger-API/plans/S6-user-accounts-and-registration.md` (scope items 5, 6 and 8) — do not modify the
> server repo. Add `TryGetCapabilitiesAsync` to `ApiSyncProvider`: `GET /auth/capabilities` with **no**
> `Authorization` header, total like the rest of the provider, cached per endpoint for the app run, and
> degrading a `404` or an unreachable server to "sign-in only" rather than an error — a server predating S6 is a
> supported thing to point at. In `MainWindowViewModel.Sync.cs` and `Views/MainWindow.axaml`, drive the auth
> panel off one auth-state property plus those capabilities: show the registration form only when
> `RegistrationEnabled`, include the bootstrap-secret field only when `RegistrationRequiresSecret`, offer "Claim
> this server" only when `HasUnclaimedAccount` (and **not** gated on `RegistrationEnabled` — an operator who
> closed registration must still be able to claim their own data), validate password length against
> `MinPasswordLength` and add no composition rules of your own, and keep the sign-in form available in every
> combination including registration-disabled. Render Plan 8's `AuthState` — `SessionExpired`,
> `SessionRevoked` and `LockedOut(until)` each get their own copy, the signed-out condition is persistent panel
> state toasted only on the transition into it (a 15-minute tick toasting every time is ~96 a day), and while
> locked out the Sign in button is **disabled with a countdown** because the server refuses the correct password
> during a lockout and retrying only burns the per-IP limit every device behind that address shares. From the
> third consecutive failed interactive sign-in, warn before sending, naming the real threshold and duration if
> `AuthCapabilitiesResponse` reports `MaxFailedLogins`/`LockoutMinutes` and staying generic otherwise — never
> hardcode 5 and 15, and warn early because the server's counter is per user, so another machine's attempts spend
> the same budget. `POST /auth/register` and `POST /auth/claim` return no token, so
> follow a success with an automatic login using the credentials already in the form. Show who is signed in from
> `LoginResponse.User`, refreshed from `/auth/whoami` when the panel opens. When a sync returns
> `AuthenticationRequired`, put the user in the sign-in form with their email prefilled and a message saying the
> session ended and why it might have — not a `Sync failed: 401` toast. Give the login failure exactly one
> message for unknown email, wrong password and unclaimed account: the server made those deliberately
> indistinguishable and the client must not undo that, even though registration honestly `409`s a duplicate
> email — comment the asymmetry so nobody "fixes" it. Make "Sign out" say plainly that it clears this device's
> token, does not revoke it server-side, and does not touch local data. Test the capability-driven visibility
> matrix, the `404` degradation, the anonymous probe, register-then-auto-login, the validation boundary, and the
> single login-failure message. Leave `/auth/password`, `/auth/devices`, `/auth/logout-all` and
> `DELETE /auth/account` to Plan 10, and change nothing about `SyncOrchestrator` or the sync wire format.
