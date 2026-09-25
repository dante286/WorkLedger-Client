# Plan 10 — Account management: devices, password, sign-out-everywhere, account deletion

> **Superseded (2026-09-25).** Everything this plan built (devices, change password, sign out everywhere,
> delete account) is removed from the client by
> [plan 12](12-account-lifecycle-to-web-portal.md) and now lives in the server's web portal
> (`WorkLedger-API/plans/S11-web-portal.md`). This file remains the design record of what shipped.

**Tickets:** [WL-139](https://mnisan.atlassian.net/browse/WL-139), under epic
[WL-105](https://mnisan.atlassian.net/browse/WL-105). Filed 2026-08-26 — nothing in the epic had owned this
work: WL-136 noted the device list and change-password only as "worth considering in the same pass", and
client-side account deletion wasn't filed at all. The server side is
[WL-108](https://mnisan.atlassian.net/browse/WL-108) / `WorkLedger-API/plans/S8-account-deletion-and-device-management.md`.

| Subtask | Scope item(s) |
|---|---|
| [WL-140](https://mnisan.atlassian.net/browse/WL-140) | 1, 6 — the five provider calls and the `SyncCoordinator` surface |
| [WL-141](https://mnisan.atlassian.net/browse/WL-141) | 2 — device list and per-device removal |
| [WL-142](https://mnisan.atlassian.net/browse/WL-142) | 3, 4 — change password, sign out everywhere |
| [WL-143](https://mnisan.atlassian.net/browse/WL-143) | 5 — delete account |
| [WL-144](https://mnisan.atlassian.net/browse/WL-144) | 7, 8 — tests and docs |

**Depends on Plans 8 and 9.** Same files as Plan 9 (`MainWindowViewModel.Sync.cs`, `Views/MainWindow.axaml`),
so strictly after it.

## Context

S8 gives the account lifecycle its remaining endpoints. All four are authenticated, all four are scoped to the
token's user, and none accepts a user identifier — so there is nothing for this client to pass but a body.

| Endpoint | Body | Returns |
|---|---|---|
| `GET /auth/devices` | — | `DeviceSummary[]` — `(DeviceId, Label, CreatedAtUtc, LastSeenUtc, IsCurrent)`, newest-seen first |
| `DELETE /auth/devices/{deviceId}` | — | `204`; `404` for unknown or another user's device |
| `POST /auth/logout-all` | — | `200` + a **fresh token for the calling device** |
| `POST /auth/password` | `ChangePasswordRequest(CurrentPassword, NewPassword)` | `200` + a **fresh token for the calling device** |
| `DELETE /auth/account` | `DeleteAccountRequest(Password, ConfirmEmail)` | `204`; `401` wrong password, `400` mismatched email |

Four things about this set are easy to get wrong, and three of them are silent:

1. **`/auth/password` and `/auth/logout-all` hand back a new token, and the client must store it.** Both bump
   the user's `SecurityStamp`, which kills every outstanding token *including the caller's own* — the fresh
   token in the response is the server making sure the app you just used doesn't log itself out. Ignore it and
   both features look like they signed you out as a side effect. Persist it through `IApiTokenStore` before
   reporting success.

2. **Removing a device does not remove its data, and that is correct.** Projects, tasks and entries belong to
   the user; `OriginDeviceId` on a row is provenance, not ownership. S8 flags this as "the single most plausible
   wrong assumption about this endpoint" — so the UI must not imply that forgetting a laptop deletes what the
   laptop recorded. Removing a device revokes its token; the device re-enrolls simply by signing in again.

3. **Deleting the account destroys the server's copy and leaves the local database completely intact.** That is
   worth stating in the confirmation *and* handling afterwards: the app still has every project, task and entry,
   and it is now pointed at an account that no longer exists. Every subsequent sync will 401 forever. The right
   post-deletion behaviour is to set `SyncMode = Off`, clear the token and email, and say plainly that local
   data was kept — not to leave a background timer grinding against a dead account.

4. **`SyncQueue` outlives the account.** Anything pending at deletion time can never be delivered. Rows stay
   `IsSynced = false` and `PruneSyncedQueueAsync` only removes synced ones, so with sync switched off they
   simply sit there — harmless, and correct if the user later signs into a different account, which is exactly
   when they'd want to be pushed. Decide this deliberately and comment it rather than discovering it.

Deletion has no undo and no grace period server-side (S8 Out of scope), and no data export beyond
`/reports/*?format=csv`, which **this client does not consume** — its Reports tab computes a local view and has
its own CSV export (`ReportsService.ToCsv`, WL-50). That local export is the honest answer to "can I keep a
copy?", and it is already in the app.

## Scope

1. **Provider methods on `ApiSyncProvider`**, all authenticated, all total, all reusing the existing
   `SendForJsonAsync` path so the Plan 8 401 semantics apply uniformly: `GetDevicesAsync`,
   `DeleteDeviceAsync(deviceId)`, `LogoutAllAsync`, `ChangePasswordAsync(current, next)`,
   `DeleteAccountAsync(password, confirmEmail)`.
   - `LogoutAllAsync` and `ChangePasswordAsync` **persist the returned token** through `IApiTokenStore` before
     returning. Test that specifically.
   - `DeleteDeviceAsync` returns `204` with no body — the existing `SendForJsonAsync<T>` deserializes a body and
     would fail on an empty one. Add a no-content send rather than inventing a response type for it.
   - A `401` from any of these is Plan 8's revocation path unchanged. Note the case that looks like a bug:
     deleting **your own** current device is allowed and means "log this device out", so a `204` immediately
     followed by 401s everywhere is the endpoint working.

2. **Device list UI** in the API sync panel, shown only when signed in. Label, last seen (localized —
   everything from the server is UTC), and *this device* marked from `IsCurrent`. The public identifier is the
   `DeviceId` GUID string, never any internal id. A *Remove* action per row, confirmed, with the confirmation
   saying that the device's recorded work stays and it can sign in again.
   - Removing the current device is the same action as signing out everywhere-but-nowhere-else; either allow it
     with a confirmation that says it will sign this app out, or leave the row's action to *Sign out*. Pick one
     and comment why — a silent guard is how the surprising-but-correct behaviour gets "fixed" later.

3. **Change password**, when signed in: current, new, confirm, validated against the `MinPasswordLength` from
   Plan 9's cached capabilities. `401` means the current password was wrong — and the token is *not* to blame,
   so do not route that into the signed-out flow. On success, store the returned token and tell the user their
   other devices have been signed out, because they have.

4. **Sign out everywhere**: `POST /auth/logout-all`, confirmed, storing the returned token. Distinguish it from
   Plan 9's local *Sign out* in the UI copy — one clears this app's token, the other kills every device's. Both
   leave local data alone.

5. **Delete account**, deliberately the hardest thing in the panel to do by accident. Behind an expander or a
   "danger zone" section, requiring the password **and** the account email typed to match (the server enforces
   both — `401` and `400` respectively; mirror the check client-side so a typo doesn't spend a failed-password
   attempt against the lockout counter). The confirmation must say, in order: the server's copy is deleted
   permanently and immediately, there is no undo, **local data on this device is kept**, and the local CSV
   export in the Reports tab is how to keep a copy of it elsewhere.
   - On `204`: clear `ApiToken`/`ApiTokenExpiresAtUtc`/`ApiEmail`, set `SyncMode = Off`, call
     `ApplyConfiguration()` so the timer actually stops, and confirm to the user that local data was kept.
   - On failure: change nothing. The server guarantees a failed guard leaves the account fully intact, and the
     client must not pre-emptively clear anything.

6. **`SyncCoordinator` surface.** These are account operations, not sync operations, but the coordinator already
   owns the `HttpClient`, the token store and the provider construction, and it is "all `MainWindowViewModel`
   sees of the sync stack" (`CLAUDE.md`). Expose them there, keeping its never-throws contract: every one
   returns a `(bool Success, string Message)` or a typed result, never an exception. Resist adding a second
   HTTP-owning service for five calls.

7. **Tests** (`RecordingHandler`, no network):
   - `logout-all` and `password` each persist the token from the response — the regression that makes both
     features look broken.
   - `password` with a wrong current password reports it and does **not** enter the signed-out state.
   - Device list parses, orders as the server sent it, and flags the current device.
   - `DELETE /auth/devices/{id}` handles `204` with an empty body; a `404` is reported as "not found", not as a
     transport failure.
   - Deleting the current device is permitted and the subsequent 401 is classified as revocation.
   - Delete account: local guard rejects a mismatched email before any request is sent; a `401`/`400` from the
     server changes no local state; a `204` clears the session, sets `SyncMode = Off`, stops the timer, and
     **leaves every local row and every `SyncQueue` row in place** (assert the row counts — this is the test
     that proves the promise in the confirmation dialog).

8. **Docs.** `CLAUDE.md`'s endpoint list and `SyncCoordinator` bullet; `README.md` wherever it describes the
   server connection. State the deletion semantics once, precisely: server copy gone, local copy kept.

## Out of scope

- **A grace period or undo for deletion.** Server-side and deliberately absent (S8 Out of scope: the follow-up
  worth having is a `DeletedAt` soft delete plus a purge job). Do not simulate one client-side by delaying the
  call.
- **A "download my data" export against `/reports/*?format=csv`.** The client doesn't consume the server's
  report endpoints by design and computes its own; the Reports tab's existing CSV export is the answer. Point at
  it, don't add a second path.
- **Renaming a device, editing the display name, changing the account email.** Not exposed server-side.
- **Admin or operator views** — listing all users, deleting someone else's account. There is no admin role in
  this model.
- **Deleting local data.** Nothing in this plan touches the SQLite database. A "start over locally" feature is a
  separate idea and a dangerous one; it does not belong bolted to the account panel.
- **`SyncOrchestrator`, the merge rule, the sync wire format.** Untouched, again.

## Acceptance criteria

- The signed-in panel lists this account's devices, marks the current one, and can remove one — with a
  confirmation that correctly says the removed device's recorded work stays and it can sign in again.
- Changing the password succeeds, keeps this app signed in (the returned token is stored), and reports that
  other devices were signed out. A wrong current password is reported as such and does not sign this app out.
- "Sign out everywhere" keeps this app signed in and is clearly distinct from the local sign-out.
- Deleting the account requires the password and the typed email, is reachable only deliberately, and its
  confirmation states that local data is kept and points at the Reports CSV export.
- After a successful deletion the app is signed out, `SyncMode` is `Off`, the sync timer is stopped, and
  **every local project, task, time entry and `SyncQueue` row is still there** — proven by a test, not asserted
  in prose.
- Any failed guard on any endpoint leaves both server and client state exactly as they were.
- Full suite green, no real network calls.

## Suggested commit message

```
feat(auth): manage devices, change password, sign out everywhere, and delete the account
```

## Prompt for Claude Code

> Plans 8 and 9 must be merged first. Read `plans/08-user-accounts-login-cutover.md`,
> `plans/09-auth-capabilities-and-account-ui.md`, and
> `WorkLedger-API/plans/S8-account-deletion-and-device-management.md` — do not modify the server repo. Add five
> authenticated methods to `ApiSyncProvider`, reusing its existing send path so Plan 8's 401 handling applies
> uniformly: `GetDevicesAsync`, `DeleteDeviceAsync` (a `204` with **no body** — add a no-content send rather
> than inventing a response type), `LogoutAllAsync`, `ChangePasswordAsync`, `DeleteAccountAsync`. **`logout-all`
> and `password` return a fresh token for this device because both bump the user's security stamp and kill the
> caller's own token — persist it through `IApiTokenStore` before reporting success, or both features will look
> like they sign the user out.** Expose them through `SyncCoordinator`, keeping its never-throws contract. In
> the signed-in panel, list the account's devices (label, localized last-seen, `IsCurrent` marked, the `DeviceId`
> GUID as the only public identifier) with a confirmed *Remove* whose wording makes clear that the removed
> device's recorded work stays on the account and it can sign in again — removing a device revokes its token,
> not its rows. Add change-password validated against the `MinPasswordLength` from Plan 9's cached capabilities,
> treating a `401` there as "wrong current password" and **not** as a revoked session. Add "sign out everywhere"
> and keep its copy distinct from the local sign-out. Put account deletion behind a danger-zone section
> requiring the password and the account email typed to match — check the email locally first so a typo doesn't
> spend a failed-password attempt against the server's lockout counter — and write the confirmation to say, in
> order, that the server's copy is deleted permanently and immediately, that there is no undo, that **local data
> on this device is kept**, and that the Reports tab's CSV export is how to keep a copy elsewhere. On a
> successful deletion clear the token/expiry/email, set `SyncMode = Off`, call `ApplyConfiguration()` so the
> timer really stops, and leave the SQLite database and the `SyncQueue` entirely alone; on any failure change no
> local state at all. Test the token-persistence regression on both endpoints, the empty-body `204`, the `404`,
> deleting your own current device, and — most importantly — that a successful account deletion leaves every
> local row and every queue row in place, asserted by count. The tickets are WL-139 and its subtasks
> WL-140–WL-144; close each as its scope item lands.
