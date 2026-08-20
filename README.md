# WorkLedger

A cross‑platform, offline‑first time tracking application focused on project‑level task management, precise time capture, and weekly reporting across desktop and mobile environments.
WorkLedger is designed for professionals who need a fast, reliable, and structured workflow for tracking work across multiple devices without sacrificing data ownership or portability.

This repository is the **client**. The backend lives in a separate repository, [**WorkLedger-API**](https://github.com/dante286/WorkLedger-API) — see [Backend](#-backend-workledger-api).

## 📍 Current Implementation Status

### ✅ Implemented

**App**
- Desktop UI (Avalonia — Windows, macOS, Linux)
- Local SQLite database via `sqlite-net-pcl`
- Project and task management, archive/unarchive
- Time tracking with a timer service, manual time entry management
- Running weekly total per project, and a per‑task total
- MVVM architecture with CommunityToolkit.Mvvm
- Light/dark/system theming
- Configurable data directory, scheduled + on‑close backups, restore from folder

**Sync engine (built, not yet exposed in the UI)**
- Per‑entity versioning: every record carries a cross‑device `SyncId`, a `Version` counter, and the `OriginDeviceId` that last wrote it
- A local `SyncQueue` — every create/update/delete is enqueued in the same transaction as the write itself
- `SyncOrchestrator`: push → pull → merge, with last‑write‑wins conflict resolution and self‑echo suppression
- Two transports behind one `ISyncProvider` abstraction:
  - **`FileSyncProvider`** — sync through a shared folder (Dropbox / OneDrive / network share), no server required
  - **`ApiSyncProvider`** — sync against a self‑hosted WorkLedger-API instance, with device‑bootstrapped JWT auth
- 117 unit/integration tests, including multi‑device merge scenarios and cross‑repo contract drift guards

**Backend** — implemented and deployable; see [WorkLedger-API](https://github.com/dante286/WorkLedger-API).

### 🚧 Not yet wired up

The sync stack above is **complete and tested but has no UI entry point**: `SyncOrchestrator` is currently
constructed only by the test suite, so nothing runs a sync at application runtime. The remaining work — a
`SyncMode` setting (Off / Folder / API), sync configuration in the Settings tab, a "Sync now" command, and
starting the orchestrator at startup — is [`plans/07-sync-reports-ui-and-release.md`](plans/07-sync-reports-ui-and-release.md).

The **Reports tab exists but is a placeholder.** The server already computes authoritative cross‑device
rollups; the client's own local rollup view is part of that same plan.

### 📋 Planned (Roadmap)

- Sync + Reports UI, then the v1.0 release (plan 7)
- Mobile apps (Android/iOS)
- Tagging system for tasks
- Client‑side analytics (focus time, context switching)
- Team mode (shared projects, permissions)

---

## 🧩 Core Goals

* Cross‑platform: Windows, macOS, Linux (desktop) + Android/iOS (mobile)
* Offline‑first: Fully functional without network connectivity
* Project‑centric: Tasks grouped under projects with weekly summaries
* Reliable sync: Conflict‑safe synchronization across devices
* Self‑hosted backend: your data in a Postgres volume you own, with no forced cloud
* No lock‑in: folder‑based sync works with no server at all
* Minimalist UI: Clean, responsive, and optimized for rapid task switching

---

## 🚀 Getting Started

### Prerequisites
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later
- Visual Studio 2022, VS Code, or JetBrains Rider
- Git

### Running Locally

1. **Clone the repository:**
   ```bash
   git clone https://github.com/dante286/WorkLedger-Client.git
   cd WorkLedger-Client
   ```

2. **Restore, build, run:**
   ```bash
   dotnet restore
   dotnet build
   dotnet run
   ```

3. **Run the tests:**
   ```bash
   dotnet test
   dotnet test --filter "FullyQualifiedName~SyncOrchestratorTests"   # a single test class
   ```

No backend is needed to run the app — it is fully functional standalone.

### Where your data lives

On first run the app creates `settings.json` and `workledger.db` in a platform data directory:

| Platform | Default location |
|---|---|
| Windows | `%LocalAppData%\WorkLedger` |
| macOS | `~/Library/Application Support/WorkLedger` |
| Linux | `$XDG_CONFIG_HOME/WorkLedger`, or `~/.config/WorkLedger` |

Settings → **Data Directory** moves both files somewhere else (a synced folder, an external drive) and
copies the existing data across immediately. Settings also configures a backup folder, a backup interval,
backup‑on‑close, and restore‑from‑folder.

### Project Structure Quick Reference

```
WorkLedger-Client/
 ├── Models/            Project, TaskItem, TimeEntry, SyncQueue (+ IVersionedEntity)
 ├── ViewModels/        MainWindowViewModel (+ .Extensions partial), TaskItemViewModel
 ├── Views/             Avalonia XAML: MainWindow, dialogs, ToastWindow
 ├── Services/          LocalDbService, SettingsService, TimerService, AppStateService,
 │                      SyncOrchestrator, ThemeService, ToastService
 ├── Sync/              ISyncProvider, FileSyncProvider, ApiSyncProvider, NullSyncProvider,
 │                      SyncResult/PullResult/RemoteChange, SyncStores
 │    └── Contracts/    Vendored copy of the server's WorkLedger.Contracts wire DTOs
 ├── Converters/        IValueConverters for XAML bindings
 ├── Assets/            Themes and fonts
 ├── Tests/             WorkLedger.Tests (xUnit)
 └── plans/             The sequenced build plans (01–07) and their index
```

### Troubleshooting
- **Build errors:** Ensure you have the .NET 10.0 SDK installed (`dotnet --version`)
- **Database issues:** Delete `workledger.db` from the data directory above to reset local data
- **UI not loading:** Check that Avalonia packages restored cleanly

---

## 🏗️ Architecture Overview

```
+-----------------------------+
|      Avalonia Client        |
|   (Desktop; mobile later)   |
+--------------+--------------+
               |
               | All writes go local first
               v
+-----------------------------+
|       Local SQLite DB       |     Every row carries SyncId, Version,
|  (Projects, Tasks, Entries) |     OriginDeviceId
+--------------+--------------+
               |
               | Each write also enqueues a change,
               | in the same transaction
               v
+-----------------------------+
|         SyncQueue           |
+--------------+--------------+
               |
               v
+-----------------------------+
|      SyncOrchestrator       |     push -> pull -> merge
| (last-write-wins, self-echo |     (not yet started at app runtime)
|  suppression, queue prune)  |
+--------------+--------------+
               |
        ISyncProvider
               |
      +--------+--------+
      |                 |
      v                 v
+-------------+   +--------------------+
|   Shared    |   |   WorkLedger-API   |   ASP.NET Core Minimal APIs
|   folder    |   |  (HTTPS + JSON,    |   + EF Core + Npgsql
| (JSON files)|   |   bearer JWT)      |
+-------------+   +---------+----------+
                            |
                            v
                  +--------------------+
                  |     PostgreSQL     |
                  | (authoritative for |
                  |  a synced account) |
                  +--------------------+
```

## 🧰 Technologies & Frameworks

### Client (this repo)

| Area | Technology | Purpose |
|---|---|---|
| Cross‑platform UI | .NET 10 + Avalonia 11 | Native UI for Windows, macOS, Linux (mobile planned) |
| Local database | SQLite via `sqlite-net-pcl` | Offline storage for projects, tasks, and time logs |
| State management | MVVM (CommunityToolkit.Mvvm) | Clean separation of UI and logic |
| Sync | `ISyncProvider` + `SyncOrchestrator` | Queue draining, delta pull, conflict resolution |
| Serialization | System.Text.Json | Change payloads and API communication |
| Tests | xUnit | Unit + multi‑device integration tests |
| Packaging | GitHub Actions | Self‑contained single‑file builds for win‑x64, linux‑x64, osx‑x64 |

### Backend ([WorkLedger-API](https://github.com/dante286/WorkLedger-API))

| Area | Technology | Purpose |
|---|---|---|
| API framework | C# / ASP.NET Core Minimal APIs (.NET 10) | Shares one language and one wire contract with the client |
| Database | PostgreSQL | Authoritative datastore for all synced data |
| ORM | EF Core + Npgsql (EF migrations) | Models, migrations, schema evolution |
| Auth | JWT bearer, device bootstrap | One token per registered device |
| Sync logic | Last‑write‑wins over versioned entities | Handles conflicts, merges, tombstones |
| Deployment | Docker / Docker Compose | Self‑hosted, portable, reproducible |

> The backend was originally specified as FastAPI + SQLAlchemy. It was **re‑specified in C#** so that the
> client and server compile the *same* DTO types, which turns wire‑contract drift into a build error instead
> of a silent sync bug.

---

## 🔄 Offline‑First Sync Architecture

WorkLedger uses a delta‑based, queue‑driven sync model.

### Entity identity and versioning

Every synced record carries three fields beyond its own data:

- **`SyncId`** (Guid) — stable cross‑device identity, generated once and never reassigned. The local
  autoincrement `Id` is unique only *within one device's database*, so two devices working offline can
  land unrelated records on the same `Id`; every sync path keys off `SyncId` instead.
- **`Version`** (int) — incremented on every local mutation, including deletes. This is the sole input to
  conflict resolution.
- **`OriginDeviceId`** — which install last wrote the row, so a device can recognise and skip echoes of
  its own changes.

Parent links are carried twice — as the local integer foreign key *and* as the parent's `SyncId` — because
only the second half means anything on another device.

### Client‑side

* All writes go to the local SQLite database first
* Each change is recorded in a `SyncQueue` row, in the same transaction as the write, with:
    * Operation type (create/update/delete)
    * Entity type (project/task/timeentry)
    * The entity's `SyncId`
    * A JSON payload of the record as written (carrying its `Version` and `OriginDeviceId`)
* `SyncOrchestrator` then, on a timer or on demand:
    * Pushes queued changes through the configured provider
    * Pulls changes made by other devices since its last cursor position
    * Merges them, resolving conflicts by version, and re‑links any child record that arrived before its parent
    * Prunes queue rows once they have been accepted

### Conflict resolution

Last‑write‑wins on the version counter, and it is implemented **identically on both sides**:

* A remote change is applied when its `Version` is strictly greater than the local record's
* A tie leaves local data untouched, which also makes replayed changes harmless
* There is deliberately **no timestamp tie‑break** — clocks between offline devices cannot be trusted
* A change originating from this device is skipped as a self‑echo before any comparison
* Deletes are tombstones that beat every peer's copy, so a delete cannot be silently resurrected

### Server‑side

* `POST /sync/push` receives a batch of changes and answers with one result per change, in request order
* `GET /sync/pull?since=` returns changes from *other* devices, with the server's own clock as the next cursor
* A losing push is answered with the server's authoritative version, so the pusher can reconcile immediately
  rather than waiting for its next pull

This ensures no data loss, no overwriting without a version check, and smooth multi‑device usage.

### Sync without a server

`FileSyncProvider` needs no backend at all: each push writes one timestamped JSON file into
`<sync folder>/changes/`, and each pull reads the files written by other devices. Point two installs at the
same Dropbox/OneDrive/network folder and they converge, using the same merge rules as the API path. Processed
files are pruned after a configurable retention window.

---

## 🖧 Backend: WorkLedger-API

The backend is a separate repository: **https://github.com/dante286/WorkLedger-API**

It is C# / ASP.NET Core Minimal APIs on .NET 10, EF Core + Npgsql → PostgreSQL, with JWT bearer auth, and it
deploys as a single `docker compose up` on a homelab box or small VPS. v1 is single‑account / multi‑device:
one account, one row per registered device.

In outline, it exposes device registration and `whoami`, the `/sync/push` and `/sync/pull` pair described
above, and three read‑only report endpoints. **The endpoint list, configuration variables, deployment
quickstart, and operational notes are documented in that repository's `README.md`** — they are deliberately
not restated here, so the two cannot drift.

Devices enrol once against a bootstrap secret and keep a long‑lived bearer token; the client persists that
token and re‑registers itself ahead of expiry.

### The shared contract

The two repositories meet at exactly one place: `WorkLedger.Contracts`, the wire DTO library authored in the
API repo. Sharing *compiled types* is the whole reason the server is C#.

Until that library is published as a package, `Sync/Contracts/` here is a **vendored copy** of it, pinned to
a known server commit — see [`Sync/Contracts/README.md`](Sync/Contracts/README.md). Those files are the
server's shape: fix problems upstream and re‑copy rather than editing them locally.
`Tests/WorkLedger.Tests/SyncContractGuardTests.cs` fails the build if the two sides stop lining up.

---

## 📊 Reporting

WorkLedger reports on:

* Total time per project
* Total time per task
* Daily breakdowns, and weekly (Monday–Sunday, UTC) summaries

The server computes these as the **authoritative cross‑device rollup** — once several devices sync into one
account, only the server holds the merged picture — and offers JSON or CSV export. See the WorkLedger-API
README for the endpoints and their parameters.

The client's Reports **tab** is a placeholder today, though the Projects tab already shows a running weekly
total. The tab will compute its own local view rather than requiring a server, so reporting works in the
offline‑first and folder‑sync cases too.

Both sides count an entry the same way — it belongs wholly to the UTC day it *started*, so an entry spanning
midnight is not split, and entries still running are excluded from totals because they have no fixed length.
One rule does **not** currently agree: the client's existing weekly total starts the week on **Sunday**, while
the server's `/reports/weekly` uses ISO weeks starting **Monday**. The client will adopt the server's ISO
Monday–Sunday week as part of the Reports work in plan 7, so that a week means the same seven days everywhere.

---

## 📦 Build Plans

The path to v1.0 is broken into seven sequenced, self‑contained plans under [`plans/`](plans/README.md) —
plans 1–6 are merged; plan 7 (Sync + Reports UI, then release) is the remainder. The server's own plans
(S0–S5) live in the API repository and are all complete. [`AGENTS.md`](AGENTS.md) covers running plans in
parallel across worktrees.

## DevOps Workflow in GitHub

Use tag prefixes in commits to ensure that Release Please picks up the commits and will appropriately handle adding commits to the PR.
PRs will be merged pretty much whenever I think one is ready.
Conventional Commits cheat sheet for your team to drive the version bumps:

| Commit prefix | Version bump |
|----------|-----------|
| fix:  | v1.0.X patch |
| feat: | v1.X.0 minor |
| feat!: or BREAKING CHANGE: | vX.0.0 major |
| chore:, docs:, refactor: | no bump |

## 📄 License

TBD — MIT, Apache 2.0, or proprietary depending on deployment strategy.
