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
- Reports tab: weekly per‑project and per‑task totals, with CSV export
- MVVM architecture with CommunityToolkit.Mvvm
- Light/dark/system theming
- Configurable data directory, scheduled + on‑close backups, restore from folder

**Sync engine**
- Per‑entity versioning: every record carries a cross‑device `SyncId`, a `Version` counter, and the `OriginDeviceId` that last wrote it
- A local `SyncQueue` — every create/update/delete is enqueued in the same transaction as the write itself
- `SyncOrchestrator`: push → pull → merge, with last‑write‑wins conflict resolution and self‑echo suppression
- Two transports behind one `ISyncProvider` abstraction:
  - **`FileSyncProvider`** — sync through a shared folder (Dropbox / OneDrive / network share), no server required
  - **`ApiSyncProvider`** — sync against a self‑hosted WorkLedger-API instance, signing in with an email and password
- Driven from the Settings tab: a **Sync Mode** switch (Off / Shared folder / WorkLedger API), device
  registration, a sync interval, a *Sync Now* button, and a last‑synced readout
- A safety backup taken before the first remote change of a sync run overwrites or deletes local data
- 157 unit/integration tests, including multi‑device merge scenarios and cross‑repo contract drift guards

**Backend** — implemented and deployable; see [WorkLedger-API](https://github.com/dante286/WorkLedger-API).

### 📋 Planned (Roadmap)

- Consuming the server's cross‑device report rollups (the Reports tab is local‑only today)
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

### Turning on sync

Sync is **off by default** — nothing leaves the machine until you choose a mode in Settings → **Sync**.

| Mode | What it needs | How it works |
|---|---|---|
| **Off** | — | The default. No provider is constructed and nothing is contacted. |
| **Shared folder** | A folder both installs can see | Each push writes one JSON file into `<folder>/changes/`; each pull reads the files the other devices wrote. No server. |
| **WorkLedger API** | A reachable [WorkLedger-API](https://github.com/dante286/WorkLedger-API) instance and an account on it | Enter your email and password and press **Sign in** once — the server issues a JWT scoped to this device and the client stores it. The password is used for that one call and is **never saved**; only the email is. This device is enrolled as a side effect of signing in. No account yet? If the server allows it, **Create an account** appears right there; on a server that already holds data from before user accounts existed, **Claim this server** takes ownership of it instead. |

The **Sync Interval** setting drives a background tick (or `Manual only`, in which case *Sync Now* is the only
trigger). **Backup Before Merge** takes a copy of `settings.json` and `workledger.db` into
`<data directory>/pre-merge-backups/` before the first incoming change of a sync run overwrites or deletes
anything local — `Once per session` by default, with the five most recent snapshots kept.

Queued changes are durable, so there is deliberately no sync-on-exit: closing the app never waits on the
network, and anything still pending goes out on the next tick or launch.

### Project Structure Quick Reference

```
WorkLedger-Client/
 ├── Models/            Project, TaskItem, TimeEntry, SyncQueue (+ IVersionedEntity)
 ├── ViewModels/        MainWindowViewModel (+ .Sync / .Reports partials), TaskItemViewModel
 ├── Views/             Avalonia XAML: MainWindow, dialogs, ToastWindow
 ├── Services/          LocalDbService, SettingsService, TimerService, AppStateService,
 │                      SyncOrchestrator, SyncCoordinator, ReportsService, ThemeService,
 │                      ToastService
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
| (last-write-wins, self-echo |     driven by SyncCoordinator, per the
|  suppression, queue prune)  |     SyncMode setting
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
| Auth | JWT bearer, email/password sign-in | Credentials are user-level; tokens are scoped to (user, device) and can be revoked |
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

In outline, it exposes sign-in, registration and account management, `whoami`, the `/sync/push` and
`/sync/pull` pair described above, and three read‑only report endpoints. **The endpoint list, configuration
variables, deployment quickstart, and operational notes are documented in that repository's `README.md`** —
they are deliberately not restated here, so the two cannot drift.

The app asks the server what it allows (`GET /auth/capabilities`) and shows only that: no registration form on
a server with signups switched off, a signup-secret field only where one is required, a password rule that is
the server's own minimum rather than a guess, and **Claim this server** only while there is unclaimed data to
take. A server too old to answer, or one that is simply unreachable, leaves a working sign-in form rather than
an error.

Signing in with an email and password mints a bearer token scoped to *this device*, and enrols the device as
a side effect. The client persists the token and the email — never the password. Because tokens can be
revoked server‑side (a password change, a sign‑out‑everywhere, this device being removed from another
machine), a rejected token means **"you have been signed out"** rather than **"try again"**: the client stops
syncing and waits for a person, rather than retrying credentials it does not have. Your work keeps saving
locally throughout, and goes up when you sign back in.

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

The client's Reports **tab** computes its own local view from the SQLite database — per‑project and per‑task
totals for the current week, exportable to CSV — so reporting works with sync switched off entirely. It does
not call the server's endpoints; consuming those cross‑device rollups is a later, additive change.

Both sides count an entry the same way: it belongs wholly to the UTC day it *started*, so an entry spanning
midnight is not split; entries still running are excluded from totals because they have no fixed length; and
a week is the **ISO week, Monday 00:00 UTC through Sunday 23:59:59 UTC**. The client computes that week in one
shared helper (`ReportsService.StartOfIsoWeek`) used by both the Projects tab's running weekly total and the
Reports tab, so the two cannot disagree with each other or with the server.

---

## 📦 Build Plans

The path to the first release is broken into seven sequenced, self‑contained plans under
[`plans/`](plans/README.md) — all seven are merged, plan 7 (Sync + Reports UI, then release) last. The
server's own plans (S0–S5) live in the API repository and are all complete. [`AGENTS.md`](AGENTS.md) covers running plans in
parallel across worktrees.

## DevOps Workflow in GitHub

Use tag prefixes in commits to ensure that Release Please picks up the commits and will appropriately handle adding commits to the PR.
PRs will be merged pretty much whenever I think one is ready.
Conventional Commits cheat sheet for your team to drive the version bumps:

| Commit prefix | Version bump |
|----------|-----------|
| fix:  | v0.1.X patch |
| feat: | v0.X.0 minor |
| feat!: or BREAKING CHANGE: | vX.0.0 major |
| chore:, docs:, refactor: | no bump |

## 📄 License

TBD — MIT, Apache 2.0, or proprietary depending on deployment strategy.
