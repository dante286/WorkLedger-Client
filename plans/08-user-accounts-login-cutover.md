# Plan 8 — User accounts: re-vendor the contract, log in instead of registering a device

**Tickets**, all under epic [WL-105](https://mnisan.atlassian.net/browse/WL-105) (*User accounts: client cutover
to login*):

| Ticket | Scope item(s) |
|---|---|
| [WL-133](https://mnisan.atlassian.net/browse/WL-133) | 1 — re-vendor `WorkLedger.Contracts` (blocks the rest) |
| [WL-135](https://mnisan.atlassian.net/browse/WL-135) | 2 — `AppSettings`: `ApiEmail`, no stored password, secret out of the sync config |
| [WL-134](https://mnisan.atlassian.net/browse/WL-134) | 3, 4 — `ApiSyncProvider` login, and the 401/423/429 policy |
| [WL-137](https://mnisan.atlassian.net/browse/WL-137) | 5, 8 — `SyncCoordinator` messaging, `SyncContractGuardTests` |
| [WL-138](https://mnisan.atlassian.net/browse/WL-138) | 6 — stamp the database with its owner, refuse a mismatch |
| [WL-145](https://mnisan.atlassian.net/browse/WL-145) (subtask of [WL-136](https://mnisan.atlassian.net/browse/WL-136)) | 7 — the plain sign-in row |

Server-side items this plan raised, tracked in the API's epic
[WL-104](https://mnisan.atlassian.net/browse/WL-104): two lockout defects on
[WL-118](https://mnisan.atlassian.net/browse/WL-118) and one additive contract change,
[WL-147](https://mnisan.atlassian.net/browse/WL-147). See item 4½.

> **WL-134's summary was corrected while planning this** (see its comment thread): it read "treat 401 as
> re-authenticate", which taken literally builds the lockout bug in scope item 4. It now reads "treat 401 as
> sign-in-required". WL-138 was filed because nothing in the epic covered scope item 6.

> **Blocked on the server.** `WorkLedger-API` plans **S6–S9** are written but **not implemented** — as of
> 2026-08-26 that repo's `main` is at `ced79aa` and S6–S9 exist only as plan files (`e67dd46`). There is nothing
> to re-vendor and no `/auth/login` to call until S9 merges and produces `plans/S9-client-handoff.md`. Do not
> start this plan against the plan documents; start it against the shipped server. Everything below is derived
> from `WorkLedger-API/plans/S6`–`S9`, so **re-read the handoff doc first and treat it as authoritative where it
> disagrees with this file**.

## Context

Today this client authenticates the way S2 designed: `POST /auth/register-device` with an operator-supplied
`X-Bootstrap-Secret` header mints a long-lived JWT bound to `(the single account, this DeviceId)`, and
`ApiSyncProvider` re-registers itself whenever the token is missing, near expiry, or rejected
(`Sync/ApiSyncProvider.cs:161`, `:350`). The server is deleting that endpoint.

After S6–S8 the server's shape is:

| Endpoint | Auth | What it is to this client |
|---|---|---|
| `POST /auth/login` | anonymous | `LoginRequest(Email, Password, DeviceId, DeviceLabel)` → `LoginResponse(Token, ExpiresAtUtc, UserSummary)`. **The only token source.** Enrolls the device as a side effect, idempotently, same semantics `register-device` had. |
| `GET /auth/whoami` | bearer | Reshaped to `(UserId, Email, DisplayName, DeviceId)`. |
| `POST /auth/register`, `POST /auth/claim`, `GET /auth/capabilities` | anonymous | Account creation and feature discovery — **Plan 9**. |
| `POST /auth/password`, `GET`/`DELETE /auth/devices`, `POST /auth/logout-all`, `DELETE /auth/account` | bearer | Account management — **Plan 10**. |
| `POST /sync/push`, `GET /sync/pull` | bearer | **Unchanged.** The sync wire format comes through S6–S8 untouched, by design. |

Three consequences drive this plan, and only the first is obvious:

1. **A token can now be killed.** S7 adds a `stamp` claim validated on every request against the user's
   `SecurityStamp`, joined to the device row. Bumping the stamp (password change, logout-all), deleting the
   device row (from another machine), or deleting the account all turn this device's token into a 401 —
   within `Auth:StampCacheSeconds` (default 30). A 401 mid-sync is no longer "my token aged out", it is
   "I have been signed out", and the two want opposite responses.

2. **Re-authentication needs a password, and we must not store one.** WL-135 is explicit: never put the
   password in `AppSettings`. So the provider's current "on 401, silently re-bootstrap and retry" has no
   credential to retry *with*. It cannot be mechanically translated into "on 401, silently re-login".

3. **Automatic retry now has teeth.** S7 adds per-user lockout: `Auth:MaxFailedLogins` (5) consecutive
   failures → locked for `Auth:LockoutMinutes` (15) → `423 Locked` with `Retry-After`, and **a locked account
   refuses the correct password too**. It also rate-limits `/auth/login` per IP
   (`Auth:LoginRateLimitPerMinute`, 10) → `429`. A background sync that answers every 401 with a login
   attempt therefore locks its own user out of their own server — five ticks at the default 15-minute
   `SyncInterval` is a little over an hour, unattended, and the user's reward for finally opening the app is a
   423. **Designing against this is the point of the plan, not a detail of it.**

There is also a data-integrity consequence of the server going multi-user that nothing on the server side can
protect us from — see scope item 6.

## Scope

### 1. Re-vendor `WorkLedger.Contracts` (WL-133) — first, and on its own commit

Copy `WorkLedger-API/src/WorkLedger.Contracts/` over `Sync/Contracts/`, keeping the existing local
conventions: upstream namespaces verbatim, explicit `using` directives added (this project has no
`ImplicitUsings`), one provenance header per file. Bump the pinned commit and the date in
`Sync/Contracts/README.md`. `ReportDtos.cs` stays un-vendored — the client computes its own reports and never
calls `/reports/*`.

Expected inventory, per S9 item 1 — **verify it against the handoff rather than trusting this table**:

| | Types |
|---|---|
| Added | `RegisterUserRequest`, `UserSummary`, `AuthCapabilitiesResponse`, `LoginRequest`, `LoginResponse`, `ChangePasswordRequest`, `DeviceSummary`, `DeleteAccountRequest` |
| Removed | `RegisterDeviceRequest`, `RegisterDeviceResponse` |
| Changed | `WhoAmIResponse(int AccountId, string DeviceId)` → `(int UserId, string Email, string? DisplayName, string DeviceId)` |
| Unchanged — **check, don't assume** | everything under `Sync/` and `Payloads/`: `SyncJson.Options`' PascalCase policy, the `SyncEntityTypes`/`SyncOperations` strings, `SyncChangeDto`, the positional `SyncPushResponse.Results`, `SyncItemResult.AuthoritativeOperation` |

That last row is the one worth real attention. If any of it moved, it moved by accident on the server side and
the fix belongs there (S9 says so in as many words) — **not** here. Editing a file under `Sync/Contracts/` to
make the client compile is the one thing the vendoring rules forbid.

This commit deliberately leaves the tree **not compiling**: `ApiSyncProvider.TryBootstrapAsync` and four
`ApiSyncProviderTests` cases reference the deleted DTOs. That is why WL-133 is a commit and not a PR, and why
this plan bundles the four tickets that put it back together.

### 2. `AppSettings`: a session, not a bootstrap secret (WL-135)

- **Add `ApiEmail`** (string, empty default, backfilled in `LoadSettings` following the `ApiEndpoint`/`ApiToken`
  pattern at `Services/SettingsService.cs:244-257`). It is the signed-in identity: prefill for the login form,
  and the "signed in as" the UI shows.
- **`ApiToken` / `ApiTokenExpiresAtUtc` stay**, with their comments rewritten — the token now comes from
  `LoginResponse`, not `RegisterDeviceResponse`.
- **Store no password.** Considered and rejected for v1: DPAPI (`ProtectedData`) is Windows-only and this ships
  three RIDs; a third-party keyring is a dependency and a packaging problem for a self-contained single-file
  publish. Tokens last `Jwt:ExpiryDays` (365 by default), so "sign in once a year, or when you're signed out"
  is an honest cost. Record the decision as a comment on `ApiEmail` and leave the seam: the provider takes
  credentials through an accessor (item 4), so an OS credential store later is one implementation of that
  accessor and nothing else changes.
- **The bootstrap secret leaves the sync configuration.** It is no longer a token-issuing credential — on the
  server it now gates only `POST /auth/register` and `POST /auth/claim`. Drop `BootstrapSecret` from
  `MainWindowViewModel.Sync.cs:59` here; Plan 9 reintroduces it as a *registration form* field gated on
  `AuthCapabilitiesResponse.RegistrationRequiresSecret`.
- **Obey the one-live-instance rule** (`CLAUDE.md`, `SettingsService` section): signing in mutates the shared
  `AppSettings` through `PersistSettings`. A login form that builds a fresh `AppSettings` from its bound
  properties blanks `DeviceId`, which re-mints on next load, which makes this install look like a new device to
  the server *and* stops it recognising its own rows as self-echoes.

### 3. `ApiSyncProvider`: log in (WL-134)

- Delete `TryBootstrapAsync`, the `BootstrapSecretHeader` constant, and the `bootstrapSecretAccessor`
  constructor parameter.
- Add `TryLoginAsync()` → `POST auth/login` with `LoginRequest(email, password, _deviceId, _deviceLabel)`,
  storing `Token`/`ExpiresAtUtc` through `IApiTokenStore` exactly as bootstrap did. `_deviceId` is still
  `AppSettings.DeviceId`, the same value stamped as `OriginDeviceId` — **do not mint a second identifier**, and
  do not send the email as the device identity.
- Return the `UserSummary` from the response so the caller can show who signed in without a second call.
- `TryWhoAmIAsync` keeps working against the reshaped `WhoAmIResponse`.

### 4. The 401 path — the design decision of this plan (WL-134)

The hazard from Context item 3 is not solved by "be careful with retries". It is solved by one observation:

> **A 401 from `POST /auth/login` is deterministic.** The credential was wrong, and the *same* credential will
> be wrong next time. Retrying it cannot succeed — it can only spend one of the five attempts standing between
> the user and a 15-minute lockout that refuses their correct password too.

So the right automated retry count for a *rejected credential* is not "a few, with backoff". It is **zero**.
Backoff is the wrong instrument entirely: it is for failures that might resolve themselves, and a wrong
password is not one. That gives a bound worth stating as an invariant and testing directly:

> **Invariant L.** Over the entire lifetime of the app, the client makes **at most one unattended failed
> `/auth/login` attempt per distinct credential presented to it** — after which that credential is discarded
> and the session goes to `SignedOut`.

Because the server's threshold is five *consecutive* failures and an unattended client can contribute at most
one per credential before discarding it, no amount of running time reaches the threshold. The invariant is also
what makes a future "remember me" safe to add: it constrains the mechanism, not the feature. "Never auto-login"
would forbid the feature and still not say what to do when a credential *is* present.

One subtlety that decides several details below: `FailedLoginCount` lives on the **`User`** row, not the device
row. Failures from a phone and a laptop share one budget, so **this client can never know the true remaining
count.** Every guess it makes must therefore be conservative, and it must never automate on the assumption that
it has attempts to spare.

**4a. One auth state, derived and owned in one place.** Define an `AuthState` on the provider/coordinator
boundary — `SignedOut`, `SignedIn`, `SessionExpired`, `SessionRevoked`, `LockedOut(until)` — and let the whole
app switch on it rather than on a scatter of booleans that can disagree. `WL-134` owns the type; `WL-137` plumbs
it through `SyncCoordinator`; `WL-146` renders it.

- `SessionExpired` is reached **without any request**: `ApiTokenExpiresAtUtc` is in the past. Free, and it means
  a year-idle install prompts instead of failing.
- `SessionRevoked` is a 401 on an authenticated call we believed was going to work.
- `LockedOut` is reachable **only from an interactive sign-in** — `423` comes from `/auth/login`, and no sync
  call ever hits that endpoint. Worth stating explicitly: under this design the background timer *cannot*
  contribute to a lockout at all, which is the property the whole section exists to establish.
- Persist nothing new for this. The token and its expiry already persist, and every state is derivable from
  them plus the last outcome. Deliberately do **not** persist `SessionRevoked`: re-checking it after a restart
  costs one authenticated request and zero login attempts, and a revocation that was actually a server restart
  should heal itself rather than leave a "you were signed out" banner the user has to dismiss.

**4b. Credentials arrive through an accessor, and a rejected one is destroyed.** `Func<ApiCredentials?>`,
mirroring the shape the bootstrap secret already uses (`Services/SyncCoordinator.cs:48`), returning null when
nothing is held. `SyncCoordinator` holds one only for the duration of an interactive sign-in. **The moment a
login returns 401, clear the held credential** before returning — that is the line of code Invariant L rests
on, so comment it as such.

**4c. The 401 rules, exhaustively.**

| Situation | What the client does |
|---|---|
| 401 on a sync call, **no** credential held | No login attempt. Report authentication-required, go to `SessionRevoked`, stop the run. |
| 401 on a sync call, credential held (interactive sign-in in flight) | Attempt login **once**. On success, retry the original call once. On 401, discard the credential and go to `SignedOut`. |
| 401 from `/auth/login` itself | Terminal. Discard the credential. Never retried automatically, ever. |
| 401 from `POST /auth/password` (Plan 10) | **Not** a revocation — it means the *current password* was wrong. Must not touch the auth state. |
| `423` | `LockedOut` until the `Retry-After` instant. No login attempt inside the run; no retry. |
| `429` | Rate-limited. Same treatment — never followed by a login. |
| Transport failure / 5xx | Retryable next tick, as today. These never touch the lockout counter, so nothing changes here. |

**4d. The timer does not fire requests at a dead session.** `SyncOrchestrator`'s timer path checks the auth
state first: anything other than `SignedIn` skips the run and returns
`SyncRunResult { Ran = false, AuthenticationRequired = true }`. A revoked install otherwise generates a doomed
round trip every 15 minutes forever. Cheap, and it makes the failure legible in logs.

**4e. Notify once per transition, not once per detection.** A skipped run every 15 minutes is 96 a day; toasted
each time it is 96 toasts. The signed-out condition is **persistent UI state** in the sync panel (Plan 9), and
the toast fires only on the *transition* into it. Getting this backwards turns a correct design into one users
switch off.

**4f. `NeedsToken()`'s renewal margin goes.** Proactive renewal needs a password we do not store, and a 365-day
token makes it near-pointless. Keep `ApiTokenExpiresAtUtc` for display and for 4a's free `SessionExpired`
check.

**4g. Plumb the signal out.** One `AuthenticationRequired` bool on `SyncResult` and `PullResult`, threaded
through `SyncOrchestrator` into `SyncRunResult`. Resist a richer failure taxonomy — there is one caller and one
thing it does. Rows hit by an auth failure are a **transport** outcome: `Failures`, still pending, never
`RejectedSyncQueueIds`. The server never saw them.

**4h. Help the human without automating for them.** The interactive path is the only one that can still lock an
account, and the server accepts that ("someone who knows the email can keep the owner locked out"). The client
can take the edge off cheaply: count consecutive failed *interactive* attempts for the current email in memory,
and from the third, warn before sending — naming the real threshold and duration if the server reports them
(see the `AuthCapabilitiesResponse` ask below), otherwise generically. Reset on success, on an email change, or
on restart. Warn at three rather than at the threshold precisely because of the shared per-user budget: the
client's count is a lower bound on the server's, never the truth.

### 4½. Server-side prerequisites — settle these before S7 is built

Designing 4 surfaced two defects and one gap in the server's plan. All three are nearly free now, while S6/S7
are still documents, and expensive afterwards — the third is a `WorkLedger.Contracts` change, which is a
two-repo change once the client has vendored it. **Raise them in `WorkLedger-API`; do not fix them here, and do
not work around them here.**

1. **`FailedLoginCount` must reset when the lockout window expires, not only on a successful login.**
   `S7` scope item 4 says only "Counters reset on success." So once an account has locked, the count stays at
   the threshold: the window lifts, the next single typo re-locks immediately, and the account is on
   one-strike-per-15-minutes for the rest of its life. Filed against **WL-118**.
2. **A `423` response must neither increment the counter nor extend `LockoutEndUtc`.** S7 says the lockout check
   runs before verification (so no increment), but says nothing about extension. A naive
   `LockoutEndUtc = now + LockoutMinutes` on every rejected attempt means anything that polls — a client, a
   script, anyone who knows the email — keeps the account locked **permanently**, and on a self-hosted box the
   locked-out user is the operator. Filed against **WL-118**.
3. **Add `MaxFailedLogins` and `LockoutMinutes` to `AuthCapabilitiesResponse`.** 4h can then warn truthfully
   instead of hardcoding 5 and 15, which an operator may have changed. Additive, anonymous, already fetched by
   the client. Filed as **WL-147** against the server epic. The client must still degrade gracefully if they
   are absent, so this is a nice-to-have, not a blocker.

### 5. `SyncCoordinator`: sign in, and say which kind of "no" this is (WL-137)

- `RegisterDeviceAsync(bootstrapSecret)` → `SignInAsync(email, password)`: persists `ApiEmail` on success,
  holds the password for the call only, and returns the same `(bool, string)` shape the view model already
  binds to.
- Add `SignOut()`: clears `ApiToken`/`ApiTokenExpiresAtUtc`, **keeps** `ApiEmail` (so the form prefills), and
  leaves `LastSyncedAtUtc` alone — the cursor is about data, not identity.
- `DescribeConfigurationProblem` (`Services/SyncCoordinator.cs:167-174`): `"Register this device first."` →
  `"Sign in first."`. And distinguish the two states WL-137 calls out — **no token** ("Sign in to sync") from
  **token rejected** ("You've been signed out — sign in again"). The second must route the user to the sign-in
  form; read as a transient sync failure it will be retried forever against a token that will never work again.
- Delete the `_bootstrapSecret` field and its `BuildProvider` capture.

### 6. Stamp the database with its owner, and refuse a mismatch (WL-138)

**This has no server-side counterpart and nothing else will catch it.** The local SQLite database has no user
column — it holds *this install's* projects, tasks and entries, and every sync path keys off `SyncId` alone.
Signing in as a different user against the same database therefore pushes user A's rows into user B's account
and pulls B's rows into a database full of A's, with no column that could ever separate them again. Under S2's
single seeded account this was unreachable, because the database had exactly one possible owner and so needed no
way to record it. After S6 the owner is a variable, and it is one typo in a login form.

**The ownership marker goes in the database, not in `AppSettings`.** The failure mode is settings and database
becoming separated, and this app has three features that separate them: backup/restore
(`RestoreFromDirectoryAsync`), the data-directory move (`BrowseDataDirectoryAsync`), and the pre-merge backups.
A guard keyed on `AppSettings.ApiEmail` passes silently the moment a database is restored next to a different
`settings.json` — which is a supported operation, not a corner case. A marker inside `workledger.db` travels
with the rows through every one of those paths.

**6a. A fourth table, one row.** `DatabaseOwner`, created in `InitializeAsync` alongside the other three,
recording: the `ApiEndpoint` it was claimed against, the server's `UserId`, the normalized email, and a
`ClaimedAtUtc`. Add it as a *new* table, not as columns on the entity tables — no migration of `Project`,
`TaskItem` or `TimeEntry`, and nothing in `LocalDbService`'s queries or the orchestrator's merge lookups
changes.

**6b. Identity comparison, and the one judgment call in it.** Treat *same account* as **matching normalized
email and matching `UserId`**. Deliberately do **not** block on a changed `ApiEndpoint`: a server that moved
from a LAN address to a domain name is common and legitimate, so record the latest endpoint and let a change
through with a warning. The residual risk this accepts is a test server and a production server that both have
the same email at the same `UserId` — plausible enough to warn about, rare enough not to block. Say so in a
comment; a future reader will otherwise "tighten" it into false positives.

**6c. No stamp means adoptable, and that path must be frictionless.** A database with no `DatabaseOwner` row is
unclaimed, and any account may claim it. **This is the offline-first happy path** — work for a week with no
server, then connect and push it all up — so it must not prompt, warn, or confirm anything. Write the stamp on
the first successful sign-in.

**6d. Migrating an existing install is a claim, not a guess.** A database that synced under the pre-S6
single-account server has no stamp and no recoverable owner: `ApiEmail` is brand new and empty, and the old
token is dead. So back-fill it as **claimed-by-unknown** (stamp present, identity blank) when
`LastSyncedAtUtc` is non-null, and let the first post-upgrade sign-in fill in the identity. This is the same
shape as the server's own `POST /auth/claim` — an existing body of data taking on an owner for the first time —
and pointing that out in the comment is the cheapest way to make it obvious rather than arbitrary. Follow
`InitializeAsync`'s existing idempotent-backfill pattern, and heed the caveat documented there: detection must
go through raw SQL against the column, because `sqlite-net` leaves a mapped property at its C# initializer
value when the column is SQL `NULL`.

**6e. The `SyncQueue` needs the same guard as the tables.** This is the one that gets missed. Pending queue rows
are addressed to whichever account they were authored under; guard the entity tables but not the queue and the
first push after a switch leaks one user's work into another's account anyway. The stamp covers the whole
database file, queue included — so state that it does, rather than leaving it to be inferred.

**6f. Reset the cursor whenever the owner changes.** `LastSyncedAtUtc` is account-scoped state living in a
global field. Carrying it across an ownership change makes the new account's first pull start from an instant
that means nothing to it, silently skipping history. Null it on adoption. (`ApiToken` and
`ApiTokenExpiresAtUtc` are account-scoped in the same way and are already cleared by `SignOut`.)

**6g. On a mismatch, refuse.** Not "confirm and proceed" — there is no confirmation wording that makes
irreversible commingling a reasonable thing for a user to consent to in a modal. Explain what was found, name
both accounts, and point at the remedy that already exists: a separate data directory via the Settings tab's
data-directory picker, which is a manual version of the per-account profiles filed as the follow-up below.
Include `ApiEmail` in `EnsureOrchestrator`'s `_builtForConfiguration` key
(`Services/SyncCoordinator.cs:178-183`) so a changed identity rebuilds the provider rather than reusing one
bound to the previous session.

**6h. Sign-out is a no-op for data.** Clear the token; keep the email, the database, the pending queue, the
cursor, and the stamp. The app has to stay fully usable with no server — that is the whole premise — so
sign-out is an auth event, never a data event.

**Two designs ruled out, in writing, so they don't come back:**

- **Archive-and-wipe on account change** (back up, clear the database, pull the new account's data down).
  This **inverts offline-first**: between sign-in and a successful pull the app is empty, an unreachable server
  leaves the user with nothing, and signing back into the first account doesn't restore their work — it sits in
  a backup folder they have to find by hand. It makes the server authoritative over local data, which is the
  one thing this codebase has spent seven plans not doing. It is also what most apps do, which is exactly why
  it needs to be refused explicitly.
- **A `UserId` column on every entity.** The textbook answer and the wrong trade here: it buys a unified
  cross-account local view nobody has asked for, and pays for it with a migration across `LocalDbService`,
  `ReportsService` and the orchestrator's merge lookups — the most load-bearing and most-tested code in the
  repo. Per-account *profiles* (the follow-up) get identical safety with the churn confined to path resolution.

**Follow-up, filed not built: [WL-148](https://mnisan.atlassian.net/browse/WL-148)** — one database per
account under `<dataDir>/profiles/`, which turns the manual data-directory remedy above into a supported
feature and gives the account-scoped settings a home. Out of scope here.

### 7. Sign-in UI — the plain half of WL-136

Enough to keep the app usable end to end, and no more. `Views/MainWindow.axaml:698-707` currently holds the
`Bootstrap Secret` + `Register this device` row inside the `IsApiSyncSelected` panel; replace it in place with
`Email` / `Password` (`PasswordChar`) / `Sign in`, plus a `Sign out` button, and repoint the existing `Status`
row (`ApiTokenStatus`) at the session. `RegisterDeviceCommand` → `SignInCommand`; clear the password property
the moment the call returns, success or failure, the way `BootstrapSecret` is cleared today
(`ViewModels/MainWindowViewModel.Sync.cs:128`).

Explicitly **not** here: the `/auth/capabilities` probe, the registration form, "Claim this server",
client-side password-length validation, and the revocation-aware routing that sends a signed-out user to the
form. That is Plan 9, and it is the difference between "works" and "good".

### 8. Tests

`Tests/WorkLedger.Tests/ApiSyncProviderTests.cs` already has the `RecordingHandler` machinery this needs; the
`BootstrapSecret` plumbing in it (`:24`, `:76`, `:101-107`) becomes credential plumbing.

- Login: happy path stores token + expiry and returns the `UserSummary`; the request body carries the
  `DeviceId` from settings, not the email.
- **Invariant L, tested directly: drive many sync cycles against a handler that 401s everything and assert the
  number of requests to `/auth/login` never exceeds one.** Run enough ticks to pass the server's threshold
  several times over — a dozen is plenty and costs nothing against a fake handler. This is the single test that
  says the lockout bug is gone, and it is the one to write first; every rule in item 4 exists to make it pass.
- A 401 with no credential available performs exactly one request — the failing one — and comes back
  `AuthenticationRequired` with the queue rows still pending. Assert the request *count*: that count is the
  thing that must not grow.
- A 401 with a credential retries login exactly once and no more, whether the retry succeeds or fails, **and
  the credential accessor is not consulted again afterwards** (4b's discard).
- `423` with `Retry-After` surfaces as `LockedOut` with the parsed instant, and triggers **no** login attempt.
- `429` surfaces as a typed failure and triggers no login attempt.
- The timer path skips entirely when the auth state is not `SignedIn` (4d): zero HTTP requests, `Ran = false`,
  `AuthenticationRequired = true`.
- `SessionExpired` is reached from a past `ApiTokenExpiresAtUtc` with **no** request made at all.
- `AuthenticationRequired` survives the trip out through `SyncOrchestrator` to `SyncRunResult`.
- Everything that made this provider total stays total: unreachable host, non-2xx, malformed JSON.
- `SyncCoordinatorTests`: sign-in without an endpoint asks for one; `SignOut` clears the token and keeps the
  email, the database, the queue and the cursor (6h).
- **Ownership (WL-138), in `LocalDbServiceTests` and `SyncCoordinatorTests`:**
  - An unstamped database is adopted on first sign-in with **no prompt at all**, and the stamp is written (6c).
    This is the offline-first path; assert the absence of friction, not just the presence of the stamp.
  - A mismatched account is refused, and **nothing** is written: no rows, no queue changes, no stamp update,
    no token stored.
  - Same email + same `UserId` against a **different** endpoint is allowed, warns, and updates the recorded
    endpoint (6b).
  - A database restored from a backup carries its stamp with it — construct the case by copying a stamped db
    file next to a *blank* `settings.json` and asserting the mismatch is still caught. **This is the test that
    justifies putting the marker in the database rather than in settings**, so name it for that.
  - The pre-S6 backfill: a database with rows and a non-null `LastSyncedAtUtc` but no stamp comes up as
    claimed-by-unknown, and the next sign-in fills in the identity (6d). Assert through raw SQL, per
    `InitializeAsync`'s documented `sqlite-net`/NULL caveat.
  - Adoption nulls `LastSyncedAtUtc` (6f).
- **`SyncContractGuardTests` (WL-137)** — the cross-repo drift guard, and the reason a botched re-vendor fails
  the build instead of the sync. Keep every existing assertion (the entity-type/operation constants, the
  three "every field populated" payload tests, both PascalCase round-trips) — they are what pins "the sync wire
  format came through S6–S8 untouched" — and add the auth shapes: `LoginRequest`/`LoginResponse` round-trip
  through `SyncJson.Options`, `WhoAmIResponse` carries `UserId` rather than `AccountId`, and
  `RegisterDeviceRequest`/`Response` are gone (their absence is compile-time, so a comment naming them is the
  honest form of that assertion).

### 9. Documentation

- `Sync/Contracts/README.md` — new pin, new date, and rewrite "What the server tells us on a rejection" only if
  the rejection shape actually changed (it shouldn't have). Add the revocation semantics: a 401 is now a
  statement about the session, not the token's age.
- `CLAUDE.md` — the endpoint list at line 41, and the `SyncCoordinator` bullet at line 96 (`RegisterDeviceAsync`
  → `SignInAsync`/`SignOut`). Add the auth model in a sentence: credentials are user-level, tokens are
  device-level, revocation is the security stamp plus the device-row check, and the client's answer to a 401 is
  to ask for a sign-in rather than retry.
- `README.md` — wherever it describes connecting to a server.

## Out of scope

- **`/auth/register`, `/auth/claim`, `/auth/capabilities`** and everything they imply for the UI — **Plan 9**.
- **`/auth/password`, `/auth/devices`, `/auth/logout-all`, `DELETE /auth/account`** — **Plan 10**.
- **Storing the password anywhere.** Not in `AppSettings`, not in a file, not "encrypted" with a key sitting
  next to it. If an OS credential store is wanted later it implements the credential accessor from item 4b and
  is its own ticket — and it is safe to add precisely because **Invariant L constrains the mechanism rather than
  the feature**: a remembered credential still gets one attempt and is then discarded, so remember-me cannot
  reintroduce the lockout storm. Do not build it here; do not design item 4 in a way that would forbid it.
- **Refresh-token rotation.** The server deliberately doesn't have it (S7 Out of scope); there is nothing to
  consume.
- **Partitioning the local database by user.** Item 6 guards against the hazard; solving it is a data-model
  plan.
- **Any change to the sync merge rule, the positional push mapping, or `AuthoritativeChanges`.** The wire
  format is unchanged and so is `SyncOrchestrator`. If something here seems to need the merge rule to move,
  that is the cross-repo alarm going off — see `CLAUDE.md`.
- **Server-side changes.** Read `WorkLedger-API` freely; land nothing in it.

## Acceptance criteria

- `Sync/Contracts/` is byte-identical to the server's `src/WorkLedger.Contracts/` at the commit recorded in
  `Sync/Contracts/README.md`, no file under it edited for a client-side reason, and `ReportDtos.cs` still
  absent.
- Nothing in `Sync/`, `Services/`, `ViewModels/`, `Views/` or `Tests/` references `register-device`,
  `X-Bootstrap-Secret`, `RegisterDeviceRequest`/`Response`, or `AccountId`.
- A device with no token signs in with an email and password, persists the JWT and its expiry, and syncs.
- **Invariant L holds and is proven by a test**: an unattended client makes at most one failed `/auth/login`
  attempt per credential, so no amount of running time can reach the server's five-failure lockout threshold.
  A rejected credential is discarded at the point of rejection.
- A 401 with no stored credential produces exactly one HTTP request, reports authentication-required, and leaves
  the queue pending.
- The background timer makes **no** HTTP request at all while the session is not `SignedIn`, and cannot
  contribute to a lockout under any circumstance.
- `423` and `429` are reported as themselves, never retried within the run, and never answered with a login.
- The signed-out condition is persistent UI state, toasted once on transition rather than once per tick.
- A revoked token reaches the UI as "you have been signed out", distinguishable from "sync is misconfigured"
  and from "the server is unreachable".
- The local database records its own owner in a `DatabaseOwner` row, so the guard survives a backup/restore, a
  data-directory move, and a lost `settings.json` — proven by a test that pairs a stamped database with a blank
  settings file.
- An unclaimed database is adopted on first sign-in with no prompt; a mismatched account is refused outright and
  leaves every local row, queue row and setting untouched.
- Signing out clears the token and nothing else: the database, the pending queue, the cursor and the stamp all
  survive, and the app stays fully usable with no server.
- `SyncContractGuardTests` passes, still asserting the sync wire format is what it was before S6.
- Full suite green, still with no real network calls.

## Suggested commit message

```
feat(sync)!: authenticate against WorkLedger-API by login instead of device bootstrap

BREAKING CHANGE: POST /auth/register-device is gone server-side. Existing installs must sign in
with an email and password; the stored device token no longer validates and the bootstrap secret
is no longer part of the sync configuration.
```

Keep the re-vendor as its own `chore(contracts):` commit ahead of it, so `git log` shows the contract moving
before the code chased it.

## Prompt for Claude Code

> Read `CLAUDE.md` (especially the cross-repo and `SettingsService` sections), then
> `WorkLedger-API/plans/S9-client-handoff.md`, `plans/S6-user-accounts-and-registration.md` and
> `plans/S7-login-and-device-enrollment.md` in the server repo — **without modifying that repo**. First commit:
> re-vendor `src/WorkLedger.Contracts/` over `Sync/Contracts/` byte-identically (upstream namespaces, explicit
> `using`s, provenance headers, no `ReportDtos.cs`) and bump the pin in `Sync/Contracts/README.md`; verify
> everything under `Sync/` and `Payloads/` is unchanged since the last pin and, if it isn't, raise it against the
> server rather than editing the vendored file. Then rewire the client: add `ApiEmail` to `AppSettings` with the
> usual backfill, drop the bootstrap secret from the sync configuration entirely, and store **no** password
> anywhere. In `ApiSyncProvider`, replace `TryBootstrapAsync` with a `TryLoginAsync` against `POST /auth/login`
> carrying `AppSettings.DeviceId` (not the email) as the device identity, and take credentials through a
> `Func<ApiCredentials?>` accessor the way the bootstrap secret was taken. **Item 4 is the point of this work —
> read it in full before writing any of it.** The governing rule is that a 401 from `/auth/login` is
> deterministic, so the correct automated retry count for a rejected credential is zero, giving Invariant L: at
> most one unattended failed login attempt per credential, ever, which can never reach the server's
> five-failure lockout threshold. Implement the `AuthState` (`SignedOut`/`SignedIn`/`SessionExpired`/
> `SessionRevoked`/`LockedOut(until)`), discard a credential the instant a login 401s (that line is what the
> invariant rests on — comment it), make the timer skip the run entirely when the state is not `SignedIn` rather
> than firing a doomed request every tick, reach `SessionExpired` from a stale `ApiTokenExpiresAtUtc` with no
> request at all, and classify `423` (respect `Retry-After`) and `429` as their own non-retryable states that
> never trigger a login. Drop the proactive expiry-margin renewal — it needs a password we don't have. Thread an
> `AuthenticationRequired` bool from `SyncResult`/`PullResult` through `SyncOrchestrator` to `SyncRunResult` so
> the UI can say "you've been signed out" instead of "sync failed: 401", toast it once per transition rather than
> once per tick, and keep auth-failed rows in `Failures` (pending), never in `RejectedSyncQueueIds`. Write the
> Invariant L test first: a dozen sync cycles against a handler that 401s everything, asserting at most one
> request ever reaches `/auth/login`. In `SyncCoordinator`, turn `RegisterDeviceAsync` into
> `SignInAsync(email, password)` plus a `SignOut()` that keeps the email, fix
> `DescribeConfigurationProblem`'s "Register this device first.", and distinguish "not signed in" from "signed in
> but revoked". Guard the local database against a change of user (item 6): the SQLite file has no user column, so
> signing in as a different email would push one user's rows into another's account irreversibly. Record the owner
> in a new one-row `DatabaseOwner` table **inside `workledger.db`**, not in `AppSettings` — a settings-based guard
> is defeated by backup/restore and the data-directory move, both of which are supported operations — comparing
> normalized email plus `UserId` and warning rather than blocking on a changed endpoint. An unstamped database is
> adopted on first sign-in with no prompt whatsoever (that is the offline-first path: work for a week offline,
> then connect); a mismatch is **refused**, not confirmed, pointing at the data-directory picker as the remedy;
> a pre-S6 database that has synced but has no stamp back-fills as claimed-by-unknown for the next sign-in to
> claim; adoption nulls `LastSyncedAtUtc`, which is account-scoped state in a global field. Sign-out clears the
> token and nothing else. Do **not** wipe-and-pull on an account change and do **not** add a `UserId` column to
> the entity tables — both are ruled out in item 6 with reasons. Include the email in `EnsureOrchestrator`'s
> configuration key. In
> the UI, replace only the bootstrap-secret/register row in `Views/MainWindow.axaml` with a plain
> email/password/Sign in/Sign out row and repoint the status text; leave `/auth/capabilities`, the registration
> form and "Claim this server" to Plan 9. Update `ApiSyncProviderTests`' `RecordingHandler` plumbing, add the
> request-count regression test for the no-credential 401, and extend `SyncContractGuardTests` with the new auth
> DTOs while keeping every assertion that pins the sync wire format unchanged. Do not touch
> `SyncOrchestrator`'s merge rule, the positional push mapping, or the server repo — but do check item 4½'s three
> server-side items (WL-118's counter reset and 423 non-extension, WL-147's capability fields) actually landed in
> the shipped server, and say so in the PR if they did not; 4h's warning wording depends on the third.
