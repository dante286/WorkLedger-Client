# Plan 7 — Sync + Reports UI, and Release (tickets TBD — see note in README.md)

## Context

This plan combines the original M6 (Sync + Reports UI) and M7 (Release) milestones — M7 is small enough
(README update + version tag) that it doesn't need its own plan, and naturally comes last once everything
else in this document has landed.

### Sync UI (part 1)

By the time this plan starts, Plans 4–6 have produced: `SyncOrchestrator` (Plan 4, not yet wired into
`App.axaml.cs` startup or exposed as a command), `FileSyncProvider` with `SyncFolderPath` +
`PickSyncFolderCommand` (Plan 5), and `ApiSyncProvider` with `ApiEndpoint`/`ApiToken` settings (Plan 6). None
of that is reachable from the UI yet. This plan adds the missing `SyncMode` switch and wires the whole stack
together — this is where `SyncOrchestrator` actually gets instantiated and driven for the first time.

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
`DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek)` (`ViewModels/MainWindowViewModel.cs:333-356`)
— reuse that same week-boundary logic for consistency rather than redefining "this week" a second way.

### Release

This repo uses Release Please (`release-please-config.json`, `release-please.yml` referenced in the
`.csproj`'s Content items, `WorkLedger-Client.csproj:45`) with Conventional Commits driving version bumps.
`AppSettings.CurrentApplicationVersion` is currently hardcoded to `"1.0.4"` (`Services/SettingsService.cs:11`)
— Release Please manages the actual version bump via commit message conventions, not a manual edit, so this
plan's release step is about correctly *landing* a `feat:`-prefixed commit/PR once M1–M6 are merged, not about
manually bumping this constant (check whether Release Please's config already handles syncing this constant on
release, e.g. via `release-please-config.json`'s `extra-files` — it doesn't currently list any, so confirm
during this plan whether the constant needs manual updating or should be added to the `extra-files` bump list).

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
     the Backup Folder row at `Views/MainWindow.axaml:536-545`) when mode is `File`; endpoint/token `TextBox`es
     when mode is `Api`. Use Avalonia's binding-driven visibility (e.g. a converter or `IsVisible` bound to a
     computed property comparing `SyncMode`) rather than hiding/showing via code-behind.
   - Read-only `LastSyncedAt` display.
   - A `Sync Now` button bound to `SyncNowCommand`, styled like the existing `Save`/`Browse` buttons.

### Reports UI
6. **Replace the Reports tab stub** (`Views/MainWindow.axaml:490-496`) with a real summary view backed by a
   new `ReportsViewModel` (or additional properties/commands on `MainWindowViewModel` if this codebase's
   convention of "one big god-viewmodel" — per `CLAUDE.md`'s architecture notes — is intentionally being kept;
   default to extending `MainWindowViewModel` for consistency with the existing pattern unless the user
   prefers to start splitting it out here).
   - **This week's total per project**: iterate `GetProjectsAsync()`, and for each project's tasks
     (`GetTasksByProjectIdAsync`), sum `TimeEntry.DurationSeconds` for entries within the current week —
     reuse the exact week-boundary calculation from `MainWindowViewModel.cs:335`
     (`DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek)`).
   - **This week's total per task**: same data, broken down one level further (per-task rather than
     per-project rollup) for the currently selected project, or all projects — pick whichever matches how the
     Reports tab is meant to be scoped (README describes "total time per project" and "total time per task"
     as separate top-level views, `README.md:199-200` — a project list with expandable per-task breakdown is
     a reasonable single-view synthesis of both).
   - Keep this read-only and derived entirely from existing `LocalDbService` query methods — no new
     persisted models or schema changes needed for this milestone.
7. **Tests**: unit-test the report aggregation logic (given a set of projects/tasks/time entries, the
   per-project and per-task weekly sums are computed correctly), independent of the UI.

### Release
8. Update `README.md`'s status section (`README.md:15-19`, the "🚧 In Progress" list currently naming "Sync
   engine (placeholder directory)") to reflect what actually landed: file-based sync live, API-based sync
   implemented-but-unconnected (no real backend yet), Reports tab implemented. Update the `Auth/`, `Sync/`,
   `LocalDb/` placeholder note (`README.md:28`) since `Sync/` is no longer just a placeholder.
9. Confirm all of Plans 1–6 (M0–M5 equivalent) are merged to `main`, then let Release Please pick up the
   accumulated `feat:`/`fix:` commits and produce its automated version-bump PR per this repo's existing
   `release-please.yml` flow — don't hand-edit version numbers outside of what Release Please's config
   manages, and confirm during this step whether `AppSettings.CurrentApplicationVersion`
   (`Services/SettingsService.cs:11`) needs adding to `release-please-config.json`'s `extra-files` so it
   doesn't silently drift from the tagged release version.

## Out of scope

- No new backend work — the Sync UI's `Api` mode only needs to *configure* `ApiSyncProvider`; there's still
  no real server for it to talk to (Plan 6's graceful-failure handling is what keeps this safe to expose in
  the UI regardless).
- No export (CSV/JSON) for reports — README mentions "optional export endpoints" (`README.md:202`) as a
  backend-side feature; this plan's Reports tab is read-only in-app display only.
- No retroactive splitting of `MainWindowViewModel` into smaller viewmodels beyond what's needed for the
  Reports tab — the three-constructor inconsistency and god-viewmodel pattern noted in `CLAUDE.md` are
  explicitly out of scope for this plan to "fix."

## Acceptance criteria

- Settings tab has a working Sync section: mode switch, mode-appropriate config fields, last-synced display,
  and a functioning Sync Now button that actually drains the queue through whichever provider is active.
- Selecting `SyncMode = File` and pointing two app instances (or two temp data directories) at the same sync
  folder results in each seeing the other's project/task/time-entry changes after a manual sync.
- Selecting `SyncMode = Api` with no real backend configured fails gracefully with a toast, not a crash.
- Reports tab shows real weekly per-project and per-task totals sourced from `LocalDbService`, matching what
  manual calculation against the same data would produce.
- Report aggregation logic has passing unit tests.
- README's status section accurately reflects the shipped state (no more "Sync engine (placeholder
  directory)" once it's real).
- A Release Please-driven version bump PR is generated/merged reflecting the accumulated `feat:` commits from
  Plans 2–7.

## Suggested commit message(s)

```
feat(sync): add SyncMode setting and wire SyncOrchestrator into app startup and Settings UI
feat(reports): replace Reports tab stub with weekly per-project/per-task summaries
docs: update README sync/reports status for v1.1.0
```

## Prompt for Claude Code

> Add a Sync section to the Settings tab in `MainWindow.axaml` mirroring the existing Backup section's style,
> bound to new `SyncMode`/`SyncFolderPath`/`ApiEndpoint`/`ApiToken`/`LastSyncedAt` properties and a
> `SyncNowCommand` on `MainWindowViewModel`, and wire `SyncOrchestrator` into `App.axaml.cs`'s startup phases
> so a real sync (using `FileSyncProvider` or `ApiSyncProvider` per the selected `SyncMode`) actually runs.
> Separately, replace the Reports tab stub (`Views/MainWindow.axaml` around line 490) with a real weekly
> summary view — total time per project and per task for the current week — using `LocalDbService`'s existing
> query methods and the same week-boundary calculation already used for the header's weekly total
> (`MainWindowViewModel.cs` around line 335). Add unit tests for the report aggregation logic. Finally, once
> this and all prior sync plans are merged, update README's status section to reflect that sync (file-based
> live, API-based ready-but-unconnected) and reporting are implemented, and confirm the Release Please-driven
> version bump reflects the accumulated `feat:` commits.
