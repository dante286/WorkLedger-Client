# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

WorkLedger is a cross-platform, offline-first time tracking desktop app built with **Avalonia UI** (.NET 10), backed by a local SQLite database.

The offline-first half is complete and shipping. The **sync half is built but not yet reachable from the UI**: `Sync/` holds a full `ISyncProvider` abstraction with two working providers (`FileSyncProvider`, `ApiSyncProvider`), and `Services/SyncOrchestrator.cs` implements the last-write-wins merge — but **`SyncOrchestrator` is constructed only in tests**. `App.axaml.cs` never instantiates it or any provider, so nothing calls `RunSyncAsync` at runtime. Wiring it up (plus a `SyncMode` setting and the Sync/Reports UI) is `plans/07-sync-reports-ui-and-release.md`, the last unstarted plan.

The backend is a **real, separate repo**, not a placeholder: [`WorkLedger-API`](#cross-repo-workledger-api), available locally at `E:\GitHub-Repos\WorkLedger-API`. It is **C# / ASP.NET Core + EF Core + Postgres** — *not* the FastAPI service `README.md` still describes (that file is stale; plan 7 item 10 corrects it).

## Commands

```bash
dotnet restore
dotnet build                                   # CI: dotnet build --configuration Release --no-restore
dotnet run                                     # launches the Avalonia desktop app
dotnet test                                    # 117 tests as of this writing; CI: --configuration Release --no-build
dotnet test --filter "FullyQualifiedName~SyncOrchestratorTests"      # one class
dotnet test --filter "FullyQualifiedName~SyncOrchestratorTests.RunSyncAsync_RemoteChangeMatchingLocalDeviceId_IsSkippedAsSelfEcho"
```

There is no `.sln`; `WorkLedger-Client.slnx` is the solution and holds **two** projects — `WorkLedger-Client.csproj` and `Tests/WorkLedger.Tests`. Bare `dotnet test` discovers the `.slnx` and works from the repo root. There's no lint/format command configured.

Packaging (mirrors `.github/workflows/package.yml`), self-contained single-file publish per RID:
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o ./publish/win-x64
# also: linux-x64, osx-x64
```

Releases are automated: `build.yml` (CI on push/PR to main) → `package.yml` (on tag `v*` / published release, installers for all 3 OSes) → `release.yml` (uploads to the GitHub release). Versioning follows **Conventional Commits** via Release Please (`fix:` → patch, `feat:` → minor, `feat!:`/`BREAKING CHANGE:` → major, `chore:`/`docs:`/`refactor:`/`style:`/`test:` → no bump). Commits reference Jira-style ticket IDs (`WL-##`).

## Cross-repo: WorkLedger-API

`E:\GitHub-Repos\WorkLedger-API` — **read it freely for reference; never modify it while working a client task.** Changes to the server belong in that repo's own branch/PR.

- Stack: ASP.NET Core Minimal APIs (`net10.0`), EF Core + Npgsql → Postgres, JWT bearer auth, xUnit + Testcontainers. All of its plans `S0`–`S5` are merged — it is complete and deployable. v1 is single-account / multi-device.
- Endpoints `ApiSyncProvider` talks to: `POST /auth/register-device` (gated by an `X-Bootstrap-Secret` header, idempotent, returns a JWT), `GET /auth/whoami`, `POST /sync/push`, `GET /sync/pull?since=`. `/reports/*` exists server-side but the client does **not** consume it — the client's own Reports tab computes a local view.
- To run it against the client: `cp .env.example .env`, fill in `Jwt__SigningKey`/`Bootstrap__Secret`/`POSTGRES_PASSWORD`, then `docker compose up -d` (API on **8080**). For server-side development instead, `docker compose -f docker-compose.dev.yml up -d` brings up only Postgres (on **15432**) and `dotnet run --project src/WorkLedger.Server` runs the API on the host. See its `README.md` for the full configuration table.

### The shared contract, and how it's vendored

The two repos meet at exactly one place: **`WorkLedger.Contracts`**, the DTO library. Sharing *compiled types* is the entire reason the server is C# rather than Python — the compiler is supposed to catch wire drift.

The API repo publishes no package yet, so `Sync/Contracts/` in this repo is a **vendored copy** of `WorkLedger-API/src/WorkLedger.Contracts/`, pinned at a known server commit (see below). Read `Sync/Contracts/README.md` before touching anything in that folder. The rules:

- **Never edit a file under `Sync/Contracts/` to fix a client-side problem.** Those files are the server's shape. Fix it in `WorkLedger-API` and re-copy.
- Type declarations are byte-identical to upstream; the only local additions are explicit `using` directives (this project doesn't enable `ImplicitUsings`) and a provenance header.
- `Tests/WorkLedger.Tests/SyncContractGuardTests.cs` asserts the vendored constants/shapes still line up with the client's own, so a rename on either side fails the build rather than the sync. `WorkLedger-Client.csproj` grants `InternalsVisibleTo("WorkLedger.Tests")` specifically so that guard can read `LocalDbService.EntityTypes`/`SyncOperations` instead of re-stating the literals.

**Vendored state (WL-100):** re-vendored 2026-08-19 and currently verbatim against upstream `2065662` (server `main` at `ced79aa`). The one shape change picked up was `SyncItemResult.AuthoritativeOperation` (WL-98) — an `"update"`/`"delete"` marker saying whether a server-wins payload is a tombstone. **It is vendored but not yet consumed:** `ApiSyncProvider` still answers a server-wins rejection by setting `SyncResult.RequiresFullPull`, which rewinds the cursor and re-pulls the account's history. Adopting the field directly is the cheaper path and is tracked as **WL-101**; adopting the *payload* without reading the field would resurrect deleted rows, which is why the rewind exists. `ReportDtos.cs` is deliberately not vendored (the client never calls `/reports/*`).

### The merge rule is a joint contract

Last-write-wins is implemented twice — `SyncOrchestrator.RemoteWins` here, `SyncService` there — and the two **must** stay identical. The normative statement lives in `WorkLedger-API/plans/S3-sync-endpoints.md`, restated in `plans/06-api-sync-provider.md`:

- Higher `Version` wins, strict `>`. **No timestamp tie-break** on either side (`RemoteChange` has no `CreatedAt` at all, so a server-side timestamp rule could not be mirrored). A tie leaves local untouched, which also makes replayed changes idempotent.
- A change whose `OriginDeviceId` equals this device's `DeviceId` is a self-echo and is skipped before any version comparison.
- Deletes tombstone; the server accepts them at `>=` and stores at `existing.Version + 1`.
- Push results are **positional** — one `SyncItemResult` per input change, in request order. `EntityId` is *not* a usable key: one batch legitimately carries create → update → delete for the same entity (WL-94).
- `Accepted = false` is **final** (invalid payload, or the server's row won). Those queue rows are retired, not retried. Only rows the provider said *nothing* about — a transport failure — stay pending.
- An accepted change may not have been written server-side at all (replay/self-echo is acked without a write, deliberately: an incidental write would restamp `UpdatedAt`, which is the pull cursor, and re-broadcast the row to every device).

Never "fix" a mismatch by quietly adjusting one side. Silent divergence in the merge rule is the exact bug class this arrangement exists to prevent.

## Architecture

**MVVM with CommunityToolkit.Mvvm**, single WinExe project (`WorkLedger-Client.csproj`, `net10.0`, nullable enabled, compiled bindings on by default). `Auth/` and `LocalDb/` are `<Folder Include>` placeholders in the csproj that don't exist on disk.

### Models and the versioning scheme

`Models/` holds plain `sqlite-net` POCOs. `Project`, `TaskItem`, and `TimeEntry` all implement `IVersionedEntity`:

- **`SyncId` (Guid)** — stable cross-device identity, generated once at creation, never reassigned. The `[PrimaryKey, AutoIncrement] Id` is unique only *within one device's database*; two offline devices routinely land unrelated entities on the same local `Id`. Every sync path (`SyncQueue.EntityId`, `RemoteChange.EntityId`, orchestrator merge lookups) keys off `SyncId` (WL-47).
- **`Version` (int)** — bumped on every local mutation, including deletes (WL-93). It is the sole input to conflict resolution.
- **`OriginDeviceId`** — the `AppSettings.DeviceId` of whichever install wrote the row.
- **Parent links are doubled** (WL-91): `TaskItem` carries both `ProjectId` (local int) and `ProjectSyncId` (Guid); `TimeEntry` carries `TaskItemId` and `TaskSyncId`. **The int half must never be trusted as received** — it's the authoring device's value. The orchestrator always recomputes it from the incoming `*SyncId`. Note `ProjectSyncId`/`TaskSyncId` deliberately have *no* `Guid.NewGuid()` initializer (unlike `SyncId`): `Guid.Empty` is the meaningful "parent unknown" sentinel that the backfills and the orphan re-link pass key off.

`SyncQueue` is the outbound change log: `EntityType`/`EntityId`(=SyncId)/`Operation`/`PayloadJson`/`IsSynced`. It carries no `Version` of its own — that lives on the serialized entity inside `PayloadJson`, and both providers lift it to the top level when putting a change on the wire.

### Services

- **`LocalDbService`** — thin wrapper over `sqlite-net-pcl`'s `SQLiteAsyncConnection`, constructed with a db path and the install's `DeviceId`.
  - **Conventions to preserve:** entity methods (`SaveProjectAsync`, etc.) insert when `Id == 0`, otherwise update. Every `SaveXAsync`/`DeleteXAsync` runs in **one transaction** that writes the row *and* inserts a matching `SyncQueue` entry, after stamping `Version`/`SyncId`/`OriginDeviceId` (`StampForInsert`/`StampForUpdate`/`StampForDelete`).
  - **Therefore: `SaveXAsync`/`DeleteXAsync` and the generic `Insert/Update/DeleteAsync<T>` are not interchangeable, and the split is load-bearing.** The generic helpers bypass stamping *and* the queue. Use `SaveXAsync`/`DeleteXAsync` for a **local edit** — something another device should learn about. Use the generic helpers for a write that is *not* a new local fact: `SyncOrchestrator`'s whole merge path (`WriteMergedAsync`, and the delete branches) and `RelinkOrphanedChildrenAsync`. Getting this backwards is silent in both directions: a local edit through the generic helpers never syncs, and a merge through `SaveXAsync` bumps the version, claims authorship, and queues the change straight back out — which made two devices trade ever-higher versions of an unchanged row forever, one push and pull per round (WL-99, fixed 2026-08-19; regression test `RunSyncAsync_TwoDevicesRelayingOneEdit_ConvergeInsteadOfTradingVersions`).
  - `EntityTypes`/`SyncOperations` are `internal static class` constants (`"project"`/`"task"`/`"timeentry"`, `"create"`/`"update"`/`"delete"`). Use them, never ad-hoc literals — they're the wire values, fixed by history already written into existing queue rows and sync files.
  - `InitializeAsync` creates all four tables and runs three idempotent backfills for older databases (`Version`/`OriginDeviceId`, then `SyncId`, then parent SyncIds — strictly in that order). It's fire-and-forget from the constructor, but exposed as the `Initialization` task so tests can await the schema. Note the recurring caveat documented there: `sqlite-net` leaves a mapped property at its C# initializer value when the column is SQL `NULL`, so backfill detection must go through raw SQL against the column, never through the ORM.
- **`SettingsService`** — owns `settings.json` and resolves the **data directory** (`%LocalAppData%/WorkLedger`, `~/Library/Application Support/WorkLedger`, or `$XDG_CONFIG_HOME`/`~/.config/WorkLedger`). Also backup/restore (`CreateBackupAsync`, `RestoreFromDirectoryAsync`, `ValidateBackupSource`) and `ValidateSyncFolder`.
  - `AppSettings` carries the sync state: `DeviceId` (minted once at first run — there is deliberately exactly **one** of these, used as both the API device identity and the `OriginDeviceId` stamp), `SyncFolderPath`, `ApiEndpoint`, `ApiToken` + `ApiTokenExpiresAtUtc` (the server-issued JWT, not a hand-entered secret), `LastSyncedAtUtc` (the pull cursor), `SyncRetentionDays`.
  - Two "value == unit" conventions: `BackupInterval`'s underlying ints *are* minutes (cast straight into `Timer.Interval`), and `SyncRetentionDays` is days. `BackupInterval.OnClose = 999` is a legacy sentinel kept only so old `settings.json` files deserialize — `LoadSettings` migrates it to the standalone `BackupOnClose` bool. Keep new values consistent with this.
- **`SyncOrchestrator`** — drives push → pull → prune against one `ISyncProvider`, on its own `System.Timers.Timer` (`ConfigureSyncInterval`, mirroring `TimerService.ConfigureBackupInterval`'s shape); `RunSyncAsync` stays directly callable as a "sync now" entry point. Its long class comment is the authoritative statement of the merge behavior — read it before changing anything here. The non-obvious parts: remote changes are applied **parents-first** (project → task → time entry) with `RelinkOrphanedChildrenAsync` catching children that arrived ahead of their parent; a failed pull leaves the cursor alone (a failed pull is not an empty one); the cursor advances to the *provider's* reported `ServerTime`, never this device's clock (WL-92); and `PruneSyncedQueueAsync` deletes `IsSynced` rows each cycle, since nothing else bounds the queue's growth.
- **`TimerService`** — the single active timer (`System.Timers.Timer`, 1s tick) plus a separate backup timer (`BackupTick` event, `RunOnCloseBackupAsync` with a timeout). Mutates `AppStateService` (`ActiveTask`/`ActiveTimeEntry`) directly; `StartTimerAsync`/`StopTimerAsync`/`SwitchTaskAsync` are the only entry points, and only one timer runs at a time. Its writes go through `LocalDbService.SaveTimeEntryAsync` so timer-created entries are queued for sync (WL-97).
- **`AppStateService`** — tiny mutable shared-state holder passed by reference into `TimerService` and `MainWindowViewModel`. Not an event bus.
- **`ThemeService`** — swaps `Assets/themes/{Light,Dark}Theme.xaml` into `Application.Current.Resources.MergedDictionaries`. Must run on the Avalonia UI thread (dispatches via `Dispatcher.UIThread` if called off-thread).
- **`ToastService`** — spawns a transient `ToastWindow` positioned relative to `desktop.MainWindow`.

### Sync layer (`Sync/`)

- **`ISyncProvider`** — `PushAsync(IEnumerable<SyncQueue>)` / `PullAsync(DateTime since)`. Implementations are expected to be **total**: a transport failure comes back as a failure-flagged `SyncResult`/`PullResult`, not a thrown exception. `SyncProviderException` is the one type anything that does throw should use — `SyncMode = Api` pointed at a dead server must not take the app down.
- **`SyncResult`** distinguishes three outcomes deliberately: `AcceptedSyncQueueIds` (retired), `RejectedSyncQueueIds` (permanently refused — also retired, else they re-push forever), and `Failures` (may be transient, so anything only listed here is retried). `RequiresFullPull` asks the orchestrator to rewind its cursor when the server's own row won.
- **`PullResult`** carries `ServerTime` (the next `since`) and `Succeeded` separately from an empty change list.
- **`ApiSyncProvider`** — HTTP against WorkLedger-API. `HttpClient` is injected, never constructed internally (one instance per provider on a sync timer is the classic socket-exhaustion bug). Handles token bootstrap/renewal: re-registers 5 minutes ahead of expiry, and retries **exactly once** on a 401. The bootstrap secret arrives via a `Func<string?>` accessor rather than a stored string, so a setup dialog can hand over a closure that returns null afterwards.
- **`FileSyncProvider`** — sync via a shared folder (Dropbox/OneDrive/network share), no backend. One JSON file per push under `<syncFolder>/changes/`, named `<deviceId>-<yyyyMMddHHmmssfff>.json`; dedup is by **filename already returned**, not by the `since` cursor (peer clocks drift). Prunes files past the retention window after every pull pass.
- **`NullSyncProvider`** — the no-op default until a `SyncMode` setting selects a real provider (plan 7).
- **`SyncStores.cs`** — `IApiTokenStore` (the provider reads/writes the token) and `ISyncCursorStore` (only the orchestrator touches this) are separate interfaces with different owners, both implemented by `SettingsSyncStore` over the real `AppSettings`. Keeping them narrow is what stops every provider test from touching `settings.json`.

### ViewModels and Views

- `ViewModelBase` is just `ObservableObject`. `MainWindowViewModel` is the single large god-viewmodel holding all app state and `[RelayCommand]`s (project/task CRUD, timer control, settings, backup/restore), split across `MainWindowViewModel.cs` and `MainWindowViewModel.Extensions.cs` via `partial class`. `TaskItemViewModel` wraps a `TaskItem` and reaches into the parent's internal `_localDbService` directly rather than through an abstraction.
- `Views/` — Avalonia XAML with minimal code-behind. Dialogs (`ProjectDialog`, `TaskDialog`, `ConfirmationDialog`) are shown via `ShowDialog<T>` on the desktop main window fetched through `Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime`. `ToastWindow` is a separate borderless window.
- `Converters/` — Avalonia `IValueConverter`s for XAML bindings (booleans → colors/visibility/chevrons/tags, seconds → duration strings, UTC → local time).

### App startup (`App.axaml.cs`)

Startup order is deliberately sequenced as commented "PHASE" blocks:
1. `DiscoverDataDirectory()` — reads only `DataDirectory` out of `settings.json` at the *default* location, without loading settings from the target location yet.
2. Construct `SettingsService` with that directory and `LoadSettings()`.
3. Construct `LocalDbService(dbPath, appSettings.DeviceId)`, then `AppStateService`, `TimerService`, `ThemeService`, `ToastService`, and finally the 6-arg `MainWindowViewModel`.
4. Register `desktop.Exit` to run a bounded (5s) on-close backup via `TimerService.RunOnCloseBackupAsync`.

There is deliberately **no** startup migration step any more: data-directory changes copy `settings.json` + `workledger.db` into the new directory immediately, in `MainWindowViewModel.BrowseDataDirectoryAsync`. The old "default → custom on every launch" copy silently overwrote the live database with a stale snapshot (WL-74). When changing data-directory/settings logic, `App.axaml.cs`, `SettingsService`, and the `BrowseDataDirectoryAsync`/`RestoreFromFolderAsync` commands must stay consistent — this area has had several fix commits (WL-46, WL-74).

### Tests (`Tests/WorkLedger.Tests`)

xUnit, referencing the app project directly (no mocking framework). Patterns to follow:

- Real SQLite files under `Path.GetTempPath()` with a GUID in the name, torn down via `IAsyncLifetime`/`IDisposable`; `await db.Initialization` before asserting on schema or backfills. File deletion retries on `IOException` (Windows file locks).
- Multi-device scenarios are built by constructing **two** `LocalDbService` instances with different `deviceId`s over different db files, and running real providers between them — see `SyncOrchestratorTests` and `FileSyncProviderTests`.
- `ApiSyncProviderTests` uses a hand-rolled `RecordingHandler : HttpMessageHandler` to script responses; `SyncOrchestratorTests` uses `FakeSyncProvider`/`FakeCursorStore`.
- `SyncContractGuardTests` is the cross-repo drift guard — if it fails, the vendored contracts and the client have diverged; fix the client or re-vendor, don't relax the assertion.

## Known inconsistencies to be aware of

- **`MainWindowViewModel` has three constructors**: a parameterless one (hardcodes `new LocalDbService("workledger.db")`, bypassing `SettingsService` — effectively dead, `App.axaml.cs` never uses it), a 5-arg one without `ToastService`, and the 6-arg one in `Extensions.cs` that `App.axaml.cs` actually calls. Prefer extending the 6-arg constructor. **WL-67** consolidates these and is a recommended prerequisite for plan 7, which threads a `SyncOrchestrator` through that same constructor.
- **Doc drift is the recurring hazard here**, since the client, the server, and the plans each describe the same contract. `README.md` was corrected on 2026-08-19 (it had claimed a FastAPI backend, no test project, and placeholder `Sync/`), and `.github/copilot-instructions.md` was deleted in the same branch rather than corrected — it had drifted the same way. That covers most of plan 7 item 10; the remainder is the status update for whatever plan 7 itself ships.
- **Week boundaries disagree across the repos.** `MainWindowViewModel.LoadTasksAsync` computes its weekly total from `DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek)` — a **Sunday**-start week (`MainWindowViewModel.cs:345`). The server's `/reports/weekly` uses ISO weeks starting **Monday** (`ReportRange.cs:49`). Entry-level counting rules do agree (banded by `StartTime`, running entries excluded). **WL-102 — resolved in the plans, not yet in code:** plan 7 now specifies adopting the server's ISO Monday rule — correct the existing header calculation, extract it as one shared helper, and have the Reports tab use it. Don't add a second week-start convention in the meantime.
- **Server-side WL-93 accommodation**: `SyncService`'s delete rule (accept at `>=`, store at `Version + 1`) was written when this client queued deletes *without* a version bump. The client now bumps (commit `2e3e379`), so that accommodation is belt-and-braces rather than load-bearing. Harmless, but don't be surprised by an accepted delete coming back at a *higher* `AuthoritativeVersion` than was pushed — that is not a rejection.

## Build plans (`plans/`)

`WorkLedger_MVP_Plan.md` is the source review; `plans/README.md` is the index + dependency graph. Read the plan file itself rather than re-deriving scope from this summary. See `AGENTS.md` for running plans concurrently with separate agents/worktrees.

| # | Plan | Status |
|---|------|--------|
| 1 | `01-housekeeping.md` — test project scaffold | merged |
| 2 | `02-data-model-versioning.md` (WL-73) — `Version`/`OriginDeviceId`/`SyncId`/`DeviceId` | merged |
| 3 | `03-sync-queue-wiring.md` — every mutation enqueues a `SyncQueue` row | merged |
| 4 | `04-sync-abstraction-orchestrator.md` — `ISyncProvider` + `SyncOrchestrator` | merged |
| 5 | `05-file-sync-provider.md` — `FileSyncProvider` | merged |
| 6 | `06-api-sync-provider.md` (WL-96) — `ApiSyncProvider` + vendored contracts | merged |
| 7 | `07-sync-reports-ui-and-release.md` — `SyncMode` setting, wire `SyncOrchestrator` into startup, real Reports tab, release | **not started** |

The plans' original WL-47–WL-54 ticket range was never created in Jira and is now occupied by unrelated tickets — create each remaining ticket fresh. Open gap tickets folded into plan 7: **WL-50** (Reports CSV export), **WL-51** (pre-merge safety backup), **WL-67** (constructor consolidation, do first). Cross-repo gaps WL-91/WL-93/WL-94 are fixed; **WL-92** (persisted, server-authoritative pull cursor) is implemented in `SyncOrchestrator`/`AppSettings` but only reaches users once plan 7 wires the orchestrator up.
