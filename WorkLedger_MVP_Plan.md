# WorkLedger — MVP Status & Build Plan

*Prepared 2026-08-06 from a review of the WorkLedger-Client repo (branch `theme-rework`, HEAD `c6b5390`), README.md, CLAUDE.md, and full commit history.*

## 1. What "MVP" means here

Per the README and CLAUDE.md, the long-term architecture is: an offline-first Avalonia desktop client with a local SQLite store, syncing through a delta-based queue to a FastAPI + Postgres backend that will be built separately. Since the backend isn't being built by this workstream, the client-side MVP goal is: **a fully usable standalone time tracker today, with a sync layer that works in file-based mode now and can point at the real API later without a rewrite.**

## 2. What's already done

The client is further along than "just a UI shell" — this is a working, daily-usable app:

- Project and task CRUD, with archive/unarchive (`Models/Project.cs`, `Models/TaskItem.cs`, `Services/LocalDbService.cs`, `ViewModels/MainWindowViewModel.cs`)
- Timer service with single-active-timer enforcement and task switching (`Services/TimerService.cs`)
- Time entries persisted per task, weekly total computed in the header (`ViewModels/MainWindowViewModel.cs` lines ~281-356)
- Local SQLite persistence via sqlite-net-pcl, tables auto-created on startup (`Services/LocalDbService.cs`)
- Settings: configurable data directory with legacy-data migration, theme switching (light/dark), app version display (`Services/SettingsService.cs`, `Services/ThemeService.cs`)
- Backup/restore: manual backup, scheduled interval backups, backup-on-close, validated restore-from-folder (`Services/SettingsService.cs`, `Services/TimerService.cs`)
- Toast notification system for save/autosave feedback (`Services/ToastService.cs`)
- MVVM throughout with CommunityToolkit.Mvvm; CI builds/packages self-contained binaries for Windows, Linux, macOS (`.github/workflows/*.yml`)
- Active theming rework in progress (current branch) — visual polish, not a functional gap

This is roughly v1.0.4 released, with a `theme-rework` branch ahead of it (**24 files modified, uncommitted** as of this review — see Housekeeping below).

## 3. What's missing for MVP

Ranked by what actually blocks calling this "done" for a solo/small-team offline-first tracker:

1. **Sync does not exist yet.** `Models/SyncQueue.cs` is defined but nothing writes to it — `LocalDbService`'s insert/update/delete paths never enqueue a change record. The `Auth/`, `Sync/`, `LocalDb/` folders referenced in the `.csproj` are empty placeholders. This is the single biggest gap between "local-only app" and the stated product vision.
2. **No conflict/versioning fields on the data models.** README describes last-write-wins with a version counter, but `Project`, `TaskItem`, and `TimeEntry` only have `UpdatedAt` — no `Version` and no device/origin identifier. Sync can't be built correctly without this.
3. **Reports tab is a stub** ("This feature will be implemented in a future release" — `Views/MainWindow.axaml` line ~490). The only reporting today is the single weekly-total figure in the header.
4. **No test project exists at all.** `dotnet test` is a no-op today per CLAUDE.md. For a sync/conflict feature this is risky to build without tests.
5. **No sync configuration UI** — Settings tab has Theme, Data Directory, version, and Backup; nothing for sync mode, folder path, or (future) API endpoint/credentials.
6. **Uncommitted work-in-progress** on `theme-rework` (24 files) should be merged or at least committed before starting new feature work, to avoid losing it and to keep `main` a clean base.

Auth is *not* on this list as a blocker — it's only required for the API-sync path, and the plan below treats it as deferred until the backend exists.

## 4. Recommended sync architecture

Since the backend is out of scope for this workstream but is coming eventually, build one abstraction with two interchangeable providers, so today's "file-based sync" and tomorrow's "API sync" are the same code path from the app's point of view:

```
ISyncProvider
    Task<SyncResult> PushAsync(IEnumerable<SyncQueue> pendingChanges)
    Task<IEnumerable<RemoteChange>> PullAsync(DateTime since)

FileSyncProvider : ISyncProvider     // writes/reads JSON change-log files in a shared folder
                                      // (Dropbox/OneDrive/network share/USB — anything that
                                      // looks like a folder to the OS)
ApiSyncProvider : ISyncProvider      // HTTPS calls to the future FastAPI backend
                                      // stubbed now, wired up when the API exists
```

A `SyncOrchestrator` (same shape as the existing backup-timer pattern in `TimerService`) drains `SyncQueue`, hands changes to whichever provider is active, applies pulled remote changes with last-write-wins conflict resolution, and updates local rows. Settings gains a `SyncMode` (`Off` / `File` / `Api`) plus mode-specific config, mirroring how `BackupDirectory`/`BackupInterval` already work.

This means: no waiting on the backend team to get real cross-device sync (two machines pointed at the same synced folder can already merge changes), and no rework when the API is ready — just flip `SyncMode` to `Api` and supply an endpoint/token.

## 5. Phased build plan

Each milestone below is scoped to be handed to Claude Code as a self-contained unit of work, following this repo's existing conventions (Conventional Commits, `WL-##` ticket references, MVVM/CommunityToolkit.Mvvm patterns, `LocalDbService` for all persistence, async/await throughout). Ticket numbers continue from the last used (`WL-46`).

### M0 — Housekeeping (WL-47)
- Commit or discard the 24 pending changes on `theme-rework`; open/merge the PR to `main` so new work branches from a clean base.
- Add a `Tests/WorkLedger.Tests` project (xUnit or NUnit — match whatever CI already assumes) wired into CI's `dotnet test` step, even if it starts with just a handful of tests. This unblocks meaningful test coverage for everything below.
- **Prompt for Claude Code:** *"Review the uncommitted changes on theme-rework, commit them with an appropriate Conventional Commit message (or tell me what's unclear), then scaffold a new xUnit test project under Tests/, add it to WorkLedger-Client.slnx, and add a `dotnet test` step to .github/workflows/build.yml."*

### M1 — Data model versioning (WL-48)
- Add `Version` (int, incremented on every update) and `OriginDeviceId` (string, a per-install GUID persisted in settings) to `Project`, `TaskItem`, `TimeEntry`.
- Add a lightweight schema-migration step in `LocalDbService.InitializeAsync` (sqlite-net-pcl doesn't auto-migrate — check for column existence and `ALTER TABLE` as needed) so existing local databases don't break.
- **Prompt for Claude Code:** *"Add Version (int) and OriginDeviceId (string) columns to Project, TaskItem, and TimeEntry in Models/. Update LocalDbService so SaveXAsync methods increment Version on update. Add a safe migration path in InitializeAsync for existing SQLite files that predate these columns. Write unit tests for the migration."*

### M2 — Wire up the sync queue (WL-49)
- Every `SaveProjectAsync`/`SaveTaskAsync`/`SaveTimeEntryAsync`/delete method in `LocalDbService` should also insert a matching `SyncQueue` row (entity type, id, operation, JSON payload, `IsSynced = false`), in the same transaction where possible.
- **Prompt for Claude Code:** *"Modify LocalDbService so every create/update/delete of Project, TaskItem, and TimeEntry also writes a corresponding SyncQueue entry with the serialized entity as PayloadJson. Add tests confirming a queue row is created for each operation type."*

### M3 — Sync abstraction + orchestrator (WL-50)
- Define `ISyncProvider` and `SyncResult`/`RemoteChange` types under a real `Sync/` folder (replacing the placeholder).
- Build `SyncOrchestrator` (in `Services/`) that: reads unsynced `SyncQueue` rows, calls the active provider's `PushAsync`, calls `PullAsync` for remote changes, applies last-write-wins merges using the new `Version`/`OriginDeviceId` fields, marks queue rows synced.
- Hook into `App.axaml.cs` startup phases and `TimerService`-style periodic ticking (or trigger manually via a "Sync now" command initially — see M6).
- **Prompt for Claude Code:** *"Create Sync/ISyncProvider.cs defining the sync provider contract, and Services/SyncOrchestrator.cs implementing push/pull/merge using SyncQueue and the new Version/OriginDeviceId fields with last-write-wins conflict resolution. Don't implement a concrete provider yet — stub it with a NullSyncProvider that no-ops, and add unit tests for the merge logic using fakes."*

### M4 — File-based sync provider (WL-51)
- Implement `FileSyncProvider`: writes each device's pending changes as timestamped JSON files into a configured shared folder (e.g. `<folder>/changes/<deviceId>-<timestamp>.json`), and on pull, reads other devices' files it hasn't processed yet.
- Add `SyncFolderPath` to `SettingsService`/settings.json, following the existing `BackupDirectory` pattern (folder picker, validation).
- **Prompt for Claude Code:** *"Implement Sync/FileSyncProvider.cs per the ISyncProvider contract from WL-50: PushAsync writes pending SyncQueue changes as a JSON file into a configured sync folder, PullAsync reads and returns changes from other devices' files not yet applied locally. Add SyncFolderPath to SettingsService mirroring how BackupDirectory already works, including a folder-picker command. Write tests covering push, pull, and the case where two devices wrote conflicting changes."*

### M5 — API sync provider (stub, WL-52)
- Implement `ApiSyncProvider` against a documented (but not-yet-real) REST contract: `POST /sync/push`, `GET /sync/pull?since=...`, bearer-token auth. Since the backend doesn't exist yet, this should build and unit-test cleanly against a mocked `HttpClient`, and fail gracefully (clear error, no crash) when pointed at a real endpoint that isn't there.
- This keeps the client ready to flip on real sync the moment the backend team ships something, without a second implementation phase.
- **Prompt for Claude Code:** *"Implement Sync/ApiSyncProvider.cs against the REST contract described in README.md's sync section (batch push of changes, pull of authoritative updates with new version numbers). Use HttpClient with a configurable base URL and bearer token stored via SettingsService. Mock the HttpClient in tests. Handle connection failures without crashing the app — surface a toast via ToastService instead."*

### M6 — Sync + Reports UI (WL-53)
- Settings tab: add a "Sync" section (mode dropdown Off/File/API, folder picker or endpoint/token fields depending on mode, last-synced timestamp, manual "Sync Now" button), following the existing Backup section's layout in `Views/MainWindow.axaml`.
- Reports tab: replace the stub with actual weekly/per-project/per-task summaries pulled from `LocalDbService` (this was in the original README goals and is currently 100% unbuilt).
- **Prompt for Claude Code:** *"Add a Sync section to the Settings tab in MainWindow.axaml mirroring the existing Backup section's style, bound to new SyncMode/SyncFolderPath/ApiEndpoint/LastSyncedAt properties and a SyncNowCommand on MainWindowViewModel. Separately, replace the Reports tab stub with a real weekly summary view: total time per project and per task for the current week, using LocalDbService's existing query methods."*

### M7 — Release (WL-54)
- Update README's status section to reflect sync now being implemented (file-based live, API-based ready-but-unconnected).
- Tag a `feat:` release (minor version bump per the repo's Conventional Commits convention) once M1-M6 land.

## 6. Sequencing notes

M0 and M1 are prerequisites for everything else and should run first. M2 and M3 can run in parallel once M1 lands. M4 and M5 both depend on M3 but not on each other — M4 (file-based) is the one that delivers real user value without any backend dependency, so prioritize it over M5 if time is constrained; M5 can slip until the backend team has something to actually point at. M6 depends on M3/M4 for the sync half but its Reports half is independent and could be pulled forward if reporting is more urgent than sync for the next release.
