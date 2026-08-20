# Plan 7 — Sync + Reports UI, and Release (tickets TBD — see note in README.md)

> **Merged 2026-08-20.** Everything below shipped, with these decisions worth recording:
>
> * **WL-67 landed first**, as recommended. `MainWindowViewModel` now has exactly one constructor;
>   `MainWindowViewModel.Extensions.cs` is gone and the new surface lives in `.Sync.cs` / `.Reports.cs`
>   partials.
> * **A `SyncCoordinator` service sits between the view model and `SyncOrchestrator`** (item 3). It owns the
>   `SyncMode` → provider mapping, the shared `HttpClient`, the pre-merge backup policy and the tick, so the
>   view model only knows "sync now" and "register this device".
> * **The settings save path was rebuilt (not in the original scope, but load-bearing here).** Both save paths
>   constructed a *fresh* `AppSettings` from the bound properties, which blanked `DeviceId`, `ApiToken` and
>   `LastSyncedAtUtc` — harmless while sync was unreachable, and a device-identity bug the moment it wasn't.
>   There is now one live `AppSettings` instance shared by `App`, the view model and `SettingsSyncStore`.
> * **`SyncInterval` was added** alongside `SyncMode` (Manual only / 5 min / 15 min / hourly), since item 4's
>   background tick needs a period and hard-coding one would have been the only alternative.
> * **`RunSyncAsync` now returns a `SyncRunResult` and takes a run lock.** A manual sync had no way to report
>   failure (the orchestrator treats one as "retry next tick"), and the timer tick could otherwise interleave
>   with the button.
> * **No on-exit sync flush** (item 4's open question): `SyncQueue` is durable, so the only thing skipping it
>   costs is latency until the next tick — against stalling app close on an unreachable server.
> * **Release:** versioning was **reset to v0.1.0** to line up with WorkLedger-API's first release. See the
>   Release section below for what that involved.

## Context

This plan combines the original M6 (Sync + Reports UI) and M7 (Release) milestones — M7 is small enough
(README update + version tag) that it doesn't need its own plan, and naturally comes last once everything
else in this document has landed.

### Sync UI (part 1)

By the time this plan starts, Plans 4–6 have produced: `SyncOrchestrator` (Plan 4, not yet wired into
`App.axaml.cs` startup or exposed as a command), `FileSyncProvider` with `SyncFolderPath` +
`PickSyncFolderCommand` (Plan 5), and `ApiSyncProvider` with `ApiEndpoint`/`ApiToken`/`ApiTokenExpiresAtUtc`
settings and device bootstrap (Plan 6). None of that is reachable from the UI yet. This plan adds the missing
`SyncMode` switch and wires the whole stack together — this is where `SyncOrchestrator` actually gets
instantiated and driven for the first time.

> **Note (2026-08-11):** `SyncOrchestrator` is still constructed *only* in tests — not in `App.axaml.cs`, not
> in `MainWindowViewModel`. So Plan 5's `FileSyncProvider` is merged but has no runtime entry point: nothing
> calls `RunSyncAsync`/`ConfigureSyncInterval` outside the test suite. Item 4 below is what makes sync real
> for users, and it is arguably the highest-value item in this plan.
>
> Plan 6 has also been rewritten against the **real** `WorkLedger-API` backend (C#/ASP.NET Core, not the
> FastAPI service the old README described). `Api` mode now means a JWT issued by
> `POST /auth/register-device`, not a hand-entered static token — see the settings-field changes in item 5.

**Recommended prerequisite (WL-52/WL-67):** this plan adds yet another constructor dependency to
`MainWindowViewModel` (a held `SyncOrchestrator`, needed by `SyncNowCommand`) on top of the existing
three-constructor mess described in `CLAUDE.md`'s "Known inconsistency" section. WL-52 flags this plan by name
as the thing that makes that mess worse. Land WL-67 (consolidating the 5-arg/6-arg constructors into one
canonical constructor) *before* starting this plan's Sync UI work, not after — threading `SyncOrchestrator`
through the current constructor soup first and cleaning up later just means redoing the same edit twice.
Doesn't have to be all of WL-52 (WL-66's dead-constructor removal and WL-68's `TaskItemViewModel` abstraction
aren't blocking), just WL-67.

**Folded in from gap review (WL-50, WL-51):** two tickets found reviewing this plan's original scope argued
for pulling their work *into* this plan rather than leaving it deferred — see the new scope items below and
the note under "Out of scope."

The Settings tab's existing **Backup** section (`Views/MainWindow.axaml:533-560`+) is the template to clone:
theme/data-directory/version fields above it, then a bolded section header (`FontWeight="600"`), then rows of
label + control + inline actions. A **Sync** section should follow directly after it in the same visual
style — same `TextBlock` header styling, same `StackPanel`/spacing resources
(`{DynamicResource SpacingLarge}`, `{DynamicResource SpacingXLarge}`), same button styling (`TealActive`
background, `Surface` background for secondary actions, per the `Save`/`Browse` buttons at
`Views/MainWindow.axaml:509-525`).

### Reports UI (part 2)

The Reports tab today is a single-line stub (`Views/MainWindow.axaml:490-496`):
```xml
<TabItem Header="Reports">
    <Border Padding="{DynamicResource SpacingXXLargeThickness}">
        <StackPanel Spacing="{DynamicResource SpacingXLarge}">
            <TextBlock ...>This feature will be implemented in a future release</TextBlock>
        </StackPanel>
    </Border>
</TabItem>
```
This half of the plan is **independent of the sync half** — it only needs `LocalDbService`'s existing query
methods (`GetProjectsAsync`, `GetTasksByProjectIdAsync`, `GetTimeEntriesByTaskIdAsync`) and could be pulled
forward ahead of the sync work if reporting matters more for the next release. There's already a working
weekly-total calculation to generalize from: `MainWindowViewModel`'s task-loading method computes a
per-selected-project weekly total by summing `TimeEntry.DurationSeconds` for entries with `StartTime` on/after
`DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek)` (`ViewModels/MainWindowViewModel.cs:333-356`).

> **Week boundary — fix this before generalizing it (WL-102, cross-repo mismatch).** That existing expression starts
> the week on **Sunday** (`DayOfWeek` numbers Sunday as 0, so it subtracts nothing on a Sunday). The server
> disagrees: `/reports/weekly` uses **ISO-8601 weeks, Monday–Sunday**, via
> `date.AddDays(-(((int)date.DayOfWeek + 6) % 7))` (`WorkLedger-API`, `src/WorkLedger.Server/Reports/ReportRange.cs:49`).
> Left alone, the client's "this week" and the server's authoritative cross-device weekly rollup cover
> different seven-day ranges and report different totals for the same account — most visibly on a Sunday,
> where the client starts a fresh week and the server is still closing the previous one.
>
> **Adopt the server's rule: ISO weeks starting Monday.** Correct the existing header calculation rather than
> writing a second one alongside it, then have the Reports tab share the corrected helper — the "one
> definition of this week" instruction still stands, it just has to be the *right* definition. Entry-level
> counting already agrees on both sides (an entry lands wholly in the UTC day it *started*, and entries with
> no `EndTime` are excluded from totals); the week start is the only divergence.

### Release

This repo uses Release Please (`release-please-config.json`) with Conventional Commits driving version bumps.

**What was actually found here (2026-08-20):** the `.csproj` referenced `.github/workflows/release-please.yml`
in its Content items, but **that workflow had never been committed** — so no automated version bump had ever
run, and the `v1.0.0`–`v1.0.4` tags were cut by hand while the packaging pipeline was being set up.
`AppSettings.CurrentApplicationVersion` was hardcoded to `"1.0.4"` while `release-please-manifest.json` said
`1.0.1`; the two had already drifted.

Resolved as follows:

* The missing `release-please.yml` workflow is committed, making Release Please the head of the
  `release-please → package → release` chain.
* `release-please-config.json` now lists `Services/SettingsService.cs` under `extra-files`, and the constant
  carries the `// x-release-please-version` annotation the generic updater looks for — so it can no longer
  drift from the tag.
* **Versioning was reset to `v0.1.0`**, matching WorkLedger-API's first release, per the explicit decision to
  treat this as the first real release of both repos. `release-please-manifest.json` is back to `0.0.0`, the
  release commit carries a `Release-As: 0.1.0` footer, and `CHANGELOG.md` keeps the old `1.0.x` entries under
  a "Superseded pre-release tags" heading rather than pretending they didn't happen. The old tags and their
  release assets still exist on GitHub; nothing continues their numbering.

## Scope

### Sync UI
1. **`SyncMode` setting.** Add an enum (`Off` / `File` / `Api`) and `AppSettings.SyncMode` (default `Off`),
   following the `BackupInterval` enum pattern (`Services/SettingsService.cs:21-31`) for how a settings enum
   is defined, persisted (with `JsonStringEnumConverter`, already registered in
   `SettingsService.LoadSettings`/`SaveSettings`), and exposed as a `ComboBox`-bindable list
   (`BackupIntervalOptions`/`ThemeOptions` are the existing examples of that binding shape in
   `MainWindowViewModel`).
2. **`LastSyncedAt`** — a persisted timestamp (`AppSettings.LastSyncedAt`, nullable `DateTime`), updated after
   each successful `SyncOrchestrator.RunSyncAsync()` call, displayed read-only in the UI.
   **This is also the pull cursor (WL-92)**, which today is an in-memory field that resets to
   `DateTime.MinValue` every launch (`Services/SyncOrchestrator.cs:38`). For `Api` mode it must be the
   **server's** `SyncPullResponse.ServerTime`, not local `UtcNow` — the server hands it back precisely so the
   cursor is immune to client clock skew. If WL-92 lands first this item is just the display half; if not,
   fold the persistence into it here rather than storing two different "last synced" values.
3. **`SyncNowCommand`** on `MainWindowViewModel`: constructs (or reuses a held) `SyncOrchestrator` with the
   provider matching the current `SyncMode` (`NullSyncProvider` for `Off`, `FileSyncProvider` for `File` using
   `SyncFolderPath`, `ApiSyncProvider` for `Api` using `ApiEndpoint`/`ApiToken`), runs it, updates
   `LastSyncedAt`, and surfaces success/failure via `ToastService` — mirroring how `SaveSettingsCommand`
   already surfaces a toast on save (check the existing command for the toast-call shape to match).
4. **Wire `SyncOrchestrator` into startup** (`App.axaml.cs`): construct it alongside the other services
   (after `localDb`, following the same phase-ordered comments style already used for the other services,
   `App.axaml.cs:61-78`), and — if `SyncMode != Off` — start a periodic tick using the same
   `ConfigureSyncInterval`-style method built in Plan 4, analogous to how `TimerService.ConfigureBackupInterval`
   is driven today. Also consider a bounded on-exit sync flush mirroring `RunOnCloseBackupAsync`
   (`App.axaml.cs:86-99`) — decide based on whether "sync on close" is worth the added exit latency; if
   skipped, note it as a deliberate choice, not an oversight.
5. **Settings tab XAML**: add a **Sync** section to `Views/MainWindow.axaml` directly below the existing
   Backup section (after line ~560), with:
   - Mode `ComboBox` bound to `SyncMode`/`SyncModeOptions`.
   - Conditionally-relevant fields: folder picker (`SyncFolderPath` + `PickSyncFolderCommand`, cloned from
     the Backup Folder row at `Views/MainWindow.axaml:536-545`) when mode is `File`. Use Avalonia's
     binding-driven visibility (e.g. a converter or `IsVisible` bound to a computed property comparing
     `SyncMode`) rather than hiding/showing via code-behind.
   - When mode is `Api`: an `ApiEndpoint` `TextBox`, a **bootstrap secret** `TextBox` (operator-supplied,
     entered once — it is the gate on `POST /auth/register-device`; prefer not persisting it), and a
     **Register this device** button that calls Plan 6's bootstrap and stores the returned JWT. The token
     itself is **issued, not typed** — show it read-only (or just show "registered as `{DeviceId}`" plus the
     expiry from `ApiTokenExpiresAtUtc`) rather than offering an editable `ApiToken` field. `AppSettings.DeviceId`
     (`Services/SettingsService.cs:19`) is the identity being registered; display it, don't let it be edited.
   - Read-only `LastSyncedAt` display.
   - A `Sync Now` button bound to `SyncNowCommand`, styled like the existing `Save`/`Browse` buttons.
6. **Pre-merge safety backup (WL-51: WL-64 + WL-65).** `SyncOrchestrator`'s last-write-wins merge applies a
   remote change directly via `SaveXAsync`/`DeleteXAsync` whenever the remote `Version` wins, with no undo
   path if a merge (or a misbehaving remote peer/provider) turns out wrong. Trigger `SettingsService`'s
   existing `CreateBackupAsync` before `SyncOrchestrator` applies its *first* remote-wins overwrite/delete in
   a given `RunSyncAsync()` call — not before the whole call, since most runs won't ever hit this path, and
   not once per overwrite, since a `RunSyncAsync()` call typically hits 0 or a handful of them at once. Add a
   setting (e.g. `PreMergeBackupMode`: `Always` / `Never` / `OncePerSession`) to control this, following the
   `BackupInterval`-enum pattern, exposed in the new Sync settings section alongside `SyncMode`.

### Reports UI
7. **Replace the Reports tab stub** (`Views/MainWindow.axaml:490-496`) with a real summary view backed by a
   new `ReportsViewModel` (or additional properties/commands on `MainWindowViewModel` if this codebase's
   convention of "one big god-viewmodel" — per `CLAUDE.md`'s architecture notes — is intentionally being kept;
   default to extending `MainWindowViewModel` for consistency with the existing pattern unless the user
   prefers to start splitting it out here).
   - **Fix the week boundary first (WL-102)**, then share it. Replace `MainWindowViewModel.cs:335`'s Sunday-start
     expression (`DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek)`) with the ISO Monday-start
     rule the server uses — `date.AddDays(-(((int)date.DayOfWeek + 6) % 7))` — and put it in **one** place
     (a `static DateTime StartOfIsoWeek(DateTime utcDate)` helper) that both the header total and the new
     Reports view call. See the week-boundary note in the Context section above for why this is a
     correctness fix and not a preference. Weeks run Monday 00:00 UTC through Sunday 23:59:59 UTC.
   - **This week's total per project**: iterate `GetProjectsAsync()`, and for each project's tasks
     (`GetTasksByProjectIdAsync`), sum `TimeEntry.DurationSeconds` for entries within the current week, using
     that shared helper.
   - **This week's total per task**: same data, broken down one level further (per-task rather than
     per-project rollup) for the currently selected project, or all projects — pick whichever matches how the
     Reports tab is meant to be scoped (README describes "total time per project" and "total time per task"
     as separate top-level views, `README.md:199-200` — a project list with expandable per-task breakdown is
     a reasonable single-view synthesis of both).
   - Keep this read-only and derived entirely from existing `LocalDbService` query methods — no new
     persisted models or schema changes needed for this milestone.
8. **CSV export (WL-50: WL-62 + WL-63).** Add an export action (button per view, or one export covering
   both) that writes the currently-displayed weekly per-project and per-task summaries to CSV via a save-file
   dialog (`topLevel.StorageProvider.SaveFilePickerAsync`, same `TopLevel.GetTopLevel(desktop.MainWindow)`
   pattern already used for the folder pickers). This aggregation is entirely local/client-side (item 7 above
   computes it from `LocalDbService` directly), so — per WL-50's own reasoning — export doesn't need to wait
   on a backend or on Plan 6's `ApiSyncProvider`; it's a client-only feature layered on item 7's data. JSON
   export was mentioned in the ticket title but isn't in `README.md`'s "optional export endpoints" description
   of the feature — CSV is the concrete ask (WL-62/WL-63); skip JSON unless it turns out to be trivial to add
   alongside.
9. **Tests**: unit-test the report aggregation logic (given a set of projects/tasks/time entries, the
   per-project and per-task weekly sums are computed correctly) and the CSV export (given a computed summary,
   the written CSV has the expected rows/columns) — both independent of the UI. Cover the week boundary
   explicitly, since that's the half most likely to regress: a Sunday resolves back to the *preceding*
   Monday (the case the old expression got wrong), a Monday resolves to itself, and an entry started at
   Sunday 23:59 UTC counts in the week that began six days earlier rather than the one starting the next day.

### Release
10. Update `README.md`'s status section (`README.md:15-19`, the "🚧 In Progress" list currently naming "Sync
   engine (placeholder directory)") to reflect what actually landed: file-based sync live, API-based sync
   live against `WorkLedger-API` (self-hosted, single-account, device-bootstrapped JWT), Reports tab
   implemented (with CSV export). Update the `Auth/`, `Sync/`, `LocalDb/` placeholder note (`README.md:28`)
   since `Sync/` is no longer just a placeholder. **Also correct the backend description**: `README.md`'s sync
   section still describes a FastAPI/SQLAlchemy service; the real backend is C#/ASP.NET Core + EF Core +
   Postgres in the `WorkLedger-API` repo, and its endpoint list should be pointed at that repo's docs rather
   than restated (and drifted) here.
11. Confirm all of Plans 1–6 (M0–M5 equivalent) are merged to `main`, then let Release Please pick up the
   accumulated `feat:`/`fix:` commits and produce its automated version-bump PR per this repo's existing
   `release-please.yml` flow — don't hand-edit version numbers outside of what Release Please's config
   manages, and confirm during this step whether `AppSettings.CurrentApplicationVersion`
   (`Services/SettingsService.cs:11`) needs adding to `release-please-config.json`'s `extra-files` so it
   doesn't silently drift from the tagged release version.

## Out of scope

- **No backend work, in either repo.** The Sync UI's `Api` mode only *configures* and drives
  `ApiSyncProvider`; the endpoints, auth, and reconciliation rules belong to `WorkLedger-API`
  (server plans S2/S3, WL-75/WL-76). Plan 6's graceful-failure handling is what keeps `Api` mode safe to
  expose in the UI even when the configured server is unreachable or not yet deployed.
- No server-side reports — `WorkLedger-API` has its own reports endpoints (server plan S4, WL-77). The
  Reports tab here stays **local-only**, computed from `LocalDbService`, and is deliberately not gated on
  sync. Consuming the server's cross-device rollups is a later, additive change.
- No retroactive splitting of `MainWindowViewModel` into smaller viewmodels beyond what's needed for the
  Reports tab — the three-constructor inconsistency and god-viewmodel pattern noted in `CLAUDE.md` are
  explicitly out of scope for this plan to "fix."

## Acceptance criteria

- Settings tab has a working Sync section: mode switch, mode-appropriate config fields, last-synced display,
  and a functioning Sync Now button that actually drains the queue through whichever provider is active.
- Selecting `SyncMode = File` and pointing two app instances (or two temp data directories) at the same sync
  folder results in each seeing the other's project/task/time-entry changes after a manual sync.
- Selecting `SyncMode = Api` with no reachable backend configured fails gracefully with a toast, not a crash.
- With a reachable `WorkLedger-API` instance, the Sync section can register the device (bootstrap secret →
  issued JWT + expiry stored) and a `Sync Now` in `Api` mode completes a push/pull round-trip.
- Before `SyncOrchestrator` applies its first remote-wins overwrite/delete in a `RunSyncAsync()` call, a
  backup is triggered per the configured `PreMergeBackupMode` (WL-51).
- Reports tab shows real weekly per-project and per-task totals sourced from `LocalDbService`, matching what
  manual calculation against the same data would produce, and can export those summaries to CSV (WL-50).
- **WL-102:** "This week" means the **ISO week starting Monday**, the same rule the server's `/reports/weekly` applies,
  and is computed by a single shared helper — the header's weekly total and the Reports tab cannot disagree,
  and neither can disagree with the server. The old Sunday-start expression is gone from the codebase.
- Report aggregation and CSV export logic both have passing unit tests, including week-boundary cases.
- README's status section accurately reflects the shipped state (no more "Sync engine (placeholder
  directory)" once it's real).
- A Release Please-driven version bump PR is generated/merged reflecting the accumulated `feat:` commits from
  Plans 2–7.

## Suggested commit message(s)

```
feat(sync): add SyncMode setting and wire SyncOrchestrator into app startup and Settings UI
feat(sync): trigger a safety backup before applying remote-wins merges
fix(reports): start the week on Monday to match the server's ISO week rule (WL-102)
feat(reports): replace Reports tab stub with weekly per-project/per-task summaries and CSV export
docs: update README sync/reports status for v1.1.0
```

## Prompt for Claude Code

> Before starting: if WL-67 (consolidating `MainWindowViewModel`'s 5-arg/6-arg constructors into one) hasn't
> landed yet, do that first — this plan adds another constructor dependency (`SyncOrchestrator`) and it's not
> worth threading it through the current constructor mess twice.
>
> Add a Sync section to the Settings tab in `MainWindow.axaml` mirroring the existing Backup section's style,
> bound to new `SyncMode`/`SyncFolderPath`/`ApiEndpoint`/`ApiToken`/`LastSyncedAt`/`PreMergeBackupMode`
> properties and a `SyncNowCommand` on `MainWindowViewModel`, and wire `SyncOrchestrator` into
> `App.axaml.cs`'s startup phases so a real sync (using `FileSyncProvider` or `ApiSyncProvider` per the
> selected `SyncMode`) actually runs. Have `SyncOrchestrator` trigger `SettingsService.CreateBackupAsync`
> before it applies its first remote-wins overwrite/delete in a given `RunSyncAsync()` call, gated by the new
> `PreMergeBackupMode` setting (Always/Never/OncePerSession).
>
> Separately, replace the Reports tab stub (`Views/MainWindow.axaml` around line 490) with a real weekly
> summary view — total time per project and per task for the current week — using `LocalDbService`'s existing
> query methods, plus a CSV export action for those summaries. "This week" must mean the **ISO week starting
> Monday**, matching the server's `/reports/weekly`: the header's existing weekly total
> (`MainWindowViewModel.cs` around line 335) currently starts the week on Sunday, so correct it to
> `date.AddDays(-(((int)date.DayOfWeek + 6) % 7))`, extract it as one shared helper, and have both the header
> and the Reports tab use it. Add unit tests for the report aggregation logic, the week boundary, and the CSV
> export. Finally, once this and all prior sync plans are merged,
> update README's status section to reflect that sync (file-based live, API-based ready-but-unconnected) and
> reporting (with export) are implemented, and confirm the Release Please-driven version bump reflects the
> accumulated `feat:` commits.
