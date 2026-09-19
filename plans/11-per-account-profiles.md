# Plan 11 — Per-account profiles: one local database per signed-in account

**Ticket:** [WL-148](https://mnisan.atlassian.net/browse/WL-148). Filed on Plan 8's branch as the deferred
follow-up to WL-138 and deliberately kept out of the user-accounts release (Plans 8–10) — WL-138 makes the
dangerous case *safe*, this makes the wanted case *pleasant*.

**Depends on Plans 8, 9 and 10.** It replaces the remedy WL-138's refusal message points at ("switch to a
separate data directory in Settings first") with a supported feature, so it has to come after the refusal
exists. No server work: the wire format, the endpoints, the merge rule and the vendored contracts are all
untouched, and `WorkLedger-API` is not opened except to read.

## Context

Signing in as a second account on one machine currently ends at a refusal. `SyncCoordinator.SignInAsync` asks
`LocalDbService.EvaluateOwnershipAsync`, gets `DifferentAccount`, discards the session it just obtained and
tells the user to point the app at a different data directory by hand. That is correct — the local database has
no user column, so a sign-in that went through would push one account's rows into the other's and pull theirs
back, permanently, with every server-side check passing — but "do it by hand, and remember which directory is
which" is a workaround, not an answer.

The shape WL-138 already argued for, and ruled out the alternatives to, is **one complete database per
account**:

```
<dataDir>/
  settings.json                     # device- and machine-scoped, one per install
  profiles.json                     # the registry: which profiles exist, which one is active
  profiles/
    local/                          # the profile every install starts in
      workledger.db
      profile.json                  # account-scoped settings
      pre-merge-backups/<ts>/
    owner-3f9a1c/
      workledger.db
      profile.json
      pre-merge-backups/<ts>/
```

Why this and not a `UserId` column on every entity, restated so it does not come back: identical safety, and
the churn is confined to **path resolution** instead of spreading across `LocalDbService`'s queries,
`ReportsService` and the orchestrator's merge lookups — the most load-bearing and most-tested code in the repo.
It also keeps each profile a complete, standalone, fully functional offline database, which a shared
multi-tenant local database is not. For an offline-first app where the local copy *is* the user's real work,
that is the difference that matters.

### The four things WL-148 says to decide rather than guess

**1. What the account key is.** WL-138 settles account identity as **normalized email + the server's `UserId`**,
with a changed endpoint warned about rather than blocked. The directory name is a stable, filesystem-safe
derivation of exactly that: the email's local part, sanitized to `[a-z0-9-]`, plus six hex characters of
`SHA-256(normalizedEmail + "|" + userId)` — legible in a file manager, collision-proof in practice, and
uniquified by the registry if it ever isn't.

But the directory name is **not** the lookup key. Profiles are found through `profiles.json`, by matching
`(UserId, normalized Email)` against the registry, so a profile's directory never has to be renamed — including
the first one, which is born as `local` before anyone has signed in and keeps that name after it is adopted.
Renaming a directory out from under an open SQLite connection is a bug waiting to happen, and there is no
reason to accept the risk for a cosmetic gain.

**2. What moves into the profile, and what stays at the root.** Account-scoped state moves: `ApiEmail`,
`ApiToken`, `ApiTokenExpiresAtUtc`, `LastSyncedAtUtc`. Everything else stays in the root `settings.json`.

`DeviceId` **stays at the root and is emphatically not per-profile.** It is the *device's* identity and the
`OriginDeviceId` stamped on every row this install writes; a per-profile `DeviceId` would make one machine look
like several to the server and break self-echo suppression in the orchestrator. The server keys device rows by
`(UserId, DeviceId)`, so the same value under two accounts is already two device rows — which is exactly right.

`ApiEndpoint` stays at the root too, which is the one debatable call here. Two accounts on the *same* server is
the case this feature is for, and a global endpoint is correct and simpler for it. Two accounts on two
different servers still works — the user changes the endpoint as part of switching — and WL-138's stamp warns
when a profile's recorded endpoint and the configured one disagree. A per-profile endpoint is a later
refinement, not a prerequisite, and making it one now would mean a new profile could be created with no
endpoint at all and no way to sign in to it.

The settings split is worth having on its own merits. Those four fields being global is a latent conflation in
today's `settings.json` regardless of profiles: a restore, a data-directory move or a pre-merge backup can
already pair a token with a database it was never issued for.

**3. Backup, restore and the data-directory picker have to say which profile they act on.** They act on the
**active** profile, and they say so. `CreateBackupAsync` copies the root `settings.json`, the active profile's
`workledger.db` and its `profile.json`; `ValidateBackupSource` keeps its current test (`settings.json` +
`workledger.db`), so backups taken before this plan still restore; `RestoreFromDirectoryAsync` restores into
the active profile. The data-directory picker moves the whole `<dataDir>` tree — every profile — which is what
it already means and what the user expects from a setting called "Data Directory".

That leaves one loose end, and it gets an invariant rather than a special case: **the `DatabaseOwner` stamp
inside the database is authoritative, and the registry entry is advisory.** Restoring account A's database into
account B's profile re-labels that profile as A's the next time it is opened, because the stamp travelled with
the rows and the registry entry did not. The same rule makes the registry self-healing after any hand-editing,
file-copying or half-finished switch.

**4. Reports, the header total and the active timer become per-profile.** They read `LocalDbService`, so this
falls out of the database swap without a line of code — which is precisely why it has to be *stated*: "my week"
means "this account's week", the Projects tab's header total and the Reports tab both follow the active
profile, and a running timer belongs to the profile it was started in.

### Switching happens live, and what makes that safe

The switch is a live one — no restart. The app already says "Restart the app to finish switching over" for a
data-directory change, and copying that here would make the feature's headline action its worst moment.

Three things make it safe, and all three are the plan's actual content:

- **The app is quiesced first.** A running work timer is stopped (and its entry saved, to the profile it was
  recorded in), the sync orchestrator's tick is stopped, and the live `AppSettings` is persisted, *before*
  anything is re-pointed. Nothing is in flight against the old database when it closes.
- **The objects keep their identity.** `LocalDbService` re-opens its connection on the new path,
  `SettingsService` re-points at the new profile directory, and the one live `AppSettings` instance has its
  four account-scoped fields reloaded in place. Every holder of those references — `TimerService`,
  `SyncCoordinator`, `SettingsSyncStore`, `MainWindowViewModel`, `TaskItemViewModel` — keeps working without
  being rebuilt, which is the whole reason this is a path-resolution change rather than a rewiring of the app.
- **The session is rebuilt, not carried.** `SyncCoordinator` drops its cached `ApiSyncProvider` and its
  `_builtForConfiguration` key on every switch. A provider holds a 401 verdict, a capability cache and a
  rejected credential; every one of those belongs to the account that is being left behind.

And one thing makes it honest: **if the switch fails for any reason, the sign-in is refused exactly as it is
today**, with WL-138's message. The guard is the invariant; profiles are the convenience built on top of it.

## Scope

### 1. The registry and the profile model (`Models/Profile.cs`)

`Profile` — `Id` (the directory name), `UserId`, `Email` (normalized), `ApiEndpoint`, `CreatedAtUtc`,
`LastUsedAtUtc`, and a `DisplayName` that falls back to something honest ("Local — not signed in") for an
unadopted profile. `ProfileRegistry` — `ActiveProfileId` plus the list, serialized to `profiles.json`.

`Profile.KeyFor(userId, email)` is the derivation in item 1 above, and `IsFor(userId, email)` mirrors
`DatabaseOwner.IsSameAccount` — the *same* comparison, deliberately expressed once in each place it is read
from, with a comment pointing at the other.

### 2. `Services/ProfileService.cs`

Owns `profiles.json`, the `profiles/` directory layout, the legacy migration, and the switch itself. Never
throws for an ordinary failure: a switch that cannot proceed comes back as a failed result, the same contract
`SyncCoordinator` keeps.

- **Migration, once, on construction.** A `<dataDir>` with no `profiles.json` is a pre-plan-11 install: create
  `profiles/local/`, **move** (not copy — two copies of one database is the WL-74 bug class) `workledger.db`
  into it, lift the four account-scoped keys out of the root `settings.json` into `profiles/local/profile.json`
  by parsing the raw JSON, and rewrite the root file without them so a token is not left behind on disk. An
  install with no database at all is the same code path with nothing to move.
- **Lookup and creation.** `FindForAccount(userId, email)`, `EnsureProfileForAccount(...)` (find, else create a
  directory and a registry entry), `AdoptActiveProfile(...)` for the unclaimed-`local` case.
- **`ReconcileActiveProfileAsync(owner)`** — invariant 3 above: the stamp wins, the registry entry is corrected
  from it.
- **`SwitchToAsync(profileId)`** — the sequence in "Switching happens live": quiesce every registered
  `IProfileAware` in registration order, persist, swap the database and the settings, update and save the
  registry, then resume them in the same order.
- **`IProfileAware`** — `OnProfileChangingAsync()` / `OnProfileChangedAsync()`. `TimerService`,
  `SyncCoordinator` and `MainWindowViewModel` implement it; registration order is the quiesce order and it
  matters (the timer writes to the database, so it stops first).

### 3. `Services/SettingsService.cs`

- A second constructor argument, `profileDirectory`, **defaulting to the data directory itself** — which is
  exactly today's layout, so every existing call site and test keeps its current meaning. `DatabasePath` and
  the new `ProfileSettingsPath` resolve against it; `SettingsPath` stays at the root.
- `UseProfile(profileDirectory)` re-points it, and `LoadProfileSettings`/`SaveProfileSettings` read and write
  `profile.json`.
- The four account-scoped properties on `AppSettings` get `[JsonIgnore]` so they are written to exactly one
  file. `LoadSettings` overlays `profile.json` after deserializing the root; `SaveSettings` writes both.
- `CreateBackupAsync` and `RestoreFromDirectoryAsync` carry `profile.json` as a third, **optional** file.

### 4. `Services/LocalDbService.cs`

`SwitchDatabaseAsync(dbPath)` — close the connection, open the new path, re-run `InitializeAsync` (so the new
profile's database gets its four tables and every backfill), and re-expose `Initialization`. Documented as
requiring a quiesced caller: it is not a concurrency primitive and must not be made to look like one.

### 5. `Services/SyncCoordinator.cs` — the sign-in rule

`SignInAsync` keeps every existing step and gains a profile decision after the login returns a user. In order,
and the order is the point:

1. The active profile's database is stamped for this account → stay. (Today's `SameAccount`/`EndpointChanged`.)
2. The registry already has a profile for this account → **switch to it.** Ahead of adoption on purpose: one
   account must never end up with two profiles.
3. The active profile is unclaimed and unregistered → **adopt it**, exactly as WL-138 already does, with no
   prompt. This is still the offline-first path and must stay frictionless.
4. Otherwise → **create a profile for this account and switch to it.**

The token needs care across a switch and gets an explicit dance rather than a comment saying "be careful":
`ApiSyncProvider` writes the issued token through `SettingsSyncStore` into the live `AppSettings` — which,
until the switch, still belongs to the *previous* profile. So capture the previous profile's token and expiry
before the login, capture the new one after it, restore the previous profile's token into its own
`profile.json`, switch, and write the new token into the new profile. Both accounts keep a working session, and
neither profile ends up holding the other's token.

Also here: the pre-merge backup root moves from `<dataDir>/pre-merge-backups` to the active profile's
directory, so one profile's five snapshots cannot evict another's.

### 6. UI — `MainWindowViewModel` + `Views/MainWindow.axaml`

A Profiles section at the top of the Settings tab, above Sync: the active profile named, the list of profiles,
and a Switch button. Plus the honesty items — backup copy that names the profile it covers, and a toast after a
switch that says which profile is now active. `MainWindowViewModel` implements `IProfileAware` and reloads
through the path `ReloadAfterSyncAsync` already provides, with the selection reset.

### 7. Startup (`App.axaml.cs`)

One new phase between discovering the data directory and constructing `SettingsService`: build
`ProfileService`, which migrates the legacy layout and resolves the active profile. `DiscoverDataDirectory()`
keeps reading the root `settings.json` and needs no second pass — `DataDirectory` never moves into a profile,
which is the reason it stays at the root.

### 8. Tests and docs

`Tests/WorkLedger.Tests/ProfileServiceTests.cs` for migration, keys, lookup, reconciliation and the switch
itself; additions to `SyncCoordinatorTests` for the four-way sign-in rule and the token dance; the existing
WL-138 refusal tests stay **exactly as they are** and keep passing, because a coordinator with no
`ProfileService` still refuses. Update `CLAUDE.md`, `plans/README.md` and this table.

## Out of scope

- **Deleting a profile, or deleting local data.** A button that erases a database belongs nowhere near a
  settings screen without its own design pass. Directories can be removed by hand.
- **Merging two profiles**, or moving work between them. The answer is the Reports tab's CSV export.
- **A per-profile `ApiEndpoint`**, per item 2 above — filed as the refinement it is if someone wants it.
- **A profile picker at launch**, or profile switching from anywhere but Settings.
- **Renaming a profile**, or a user-chosen display name. The account's email is the name.
- **Anything in `Sync/`** beyond `SyncCoordinator`: the providers, the orchestrator, the merge rule and the
  vendored contracts are untouched. So is the server.

## Acceptance criteria

- A fresh install starts in the `local` profile and behaves exactly as it does today, including the
  no-prompt-at-all adoption on first sign-in.
- An existing install's `workledger.db` and account settings are migrated into `profiles/local/` once, with no
  data copied twice and no token left in the root `settings.json`.
- Signing in as a second account switches to (or creates) that account's profile live — no restart — and the
  first account's projects, tasks, entries and pending queue rows are still exactly where they were when
  switched back to.
- One account never has two profiles, and a profile never holds another account's token.
- A failed switch refuses the sign-in with WL-138's message and changes nothing.
- The `DatabaseOwner` stamp wins over the registry: a restored database re-labels its profile.
- `DeviceId` is identical across every profile on the install.
- Full suite green, no real network calls.

## Suggested commit message

```
feat(profiles): give each signed-in account its own local database
```

## Prompt for Claude Code

> Plans 8–10 must be merged first. Read `plans/11-per-account-profiles.md` and WL-138's section in
> `plans/08-user-accounts-login-cutover.md`; do not modify `WorkLedger-API`. Give each account its own complete
> SQLite database under `<dataDir>/profiles/<id>/`, with a `profiles.json` registry at the root recording which
> profiles exist and which is active. Move `ApiEmail`, `ApiToken`, `ApiTokenExpiresAtUtc` and `LastSyncedAtUtc`
> into a per-profile `profile.json`; leave everything else — **especially `DeviceId`**, which is the device's
> identity and the `OriginDeviceId` on every row — in the root `settings.json`. Migrate an existing install once
> by moving its database into `profiles/local/` and lifting those four keys across. Make the switch live rather
> than restart-based: quiesce (stop the work timer, stop the sync tick, persist settings), re-open
> `LocalDbService` on the new path, re-point `SettingsService`, reload the four fields into the one live
> `AppSettings` instance, then resume — object identity is preserved throughout, so nothing is rebuilt. In
> `SignInAsync`, replace WL-138's refusal with a four-way decision — stamped for this account, stay; a profile
> already registered for it, switch; unclaimed and unregistered, adopt; otherwise create and switch — and keep
> the refusal as the fallback when the switch fails, because the guard is the invariant and profiles are the
> convenience on top of it. Mind the token: the provider writes the issued token into the live settings before
> the switch, so capture and restore the previous profile's token into its own `profile.json` and write the new
> one into the new profile. Treat the `DatabaseOwner` stamp as authoritative and the registry entry as advisory,
> so a restored database re-labels its profile. Add a Profiles section to the Settings tab, move the pre-merge
> backups under the active profile, and make backup/restore say which profile they act on. Test migration, the
> four-way rule, the token dance, and that switching away and back leaves every row and every queue row intact;
> the existing WL-138 refusal tests must keep passing untouched. The ticket is WL-148.
