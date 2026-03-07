# WorkLedger
A cross‑platform, offline‑first time tracking application focused on project‑level task management, precise time capture, and weekly reporting across desktop and mobile environments.
WorkLedger is designed for professionals who need a fast, reliable, and structured workflow for tracking work across multiple devices without sacrificing data ownership or portability.

## 🧩 Core Goals
* Cross‑platform: Windows, macOS, Linux (desktop) + Android/iOS (mobile)
* Offline‑first: Fully functional without network connectivity
* Project‑centric: Tasks grouped under projects with weekly summaries
* Reliable sync: Conflict‑safe synchronization across devices
* Self‑hosted backend: FastAPI + Postgres for full control and extensibility
* Minimalist UI: Clean, responsive, and optimized for rapid task switching


## 🏗️ Architecture Overview
High‑Level Architecture
```
+---------------------------+
|      Avalonia Client     |
|  (Desktop & Mobile Apps) |
+------------+--------------+
             |
             | Offline-first data layer
             v
+---------------------------+
|     Local SQLite DB      |
|  (Tasks, Projects, Logs) |
+------------+--------------+
             |
             | Background Sync Queue
             v
+---------------------------+
|     Sync Orchestrator    |
| (Conflict resolution,     |
|  delta updates, retries)  |
+------------+--------------+
             |
             | HTTPS (JSON)
             v
+---------------------------+
|        FastAPI API       |
|  (Auth, Sync, Projects,  |
|   Tasks, Reports)        |
+------------+--------------+
             |
             | SQLAlchemy ORM
             v
+---------------------------+
|         Postgres         |
| (Authoritative datastore)|
+---------------------------+
```


## 🧰 Technologies & Frameworks

### Frontend / Client

| Area | Technology |  Purpose |
|----------|----------|----------|
| Cross‑platform UI | .NET Avalonia (App project template) | Native UI for Windows, macOS, Linux, Android, iOS |
| Local database | SQLite | Offline storage for tasks, projects, and time logs |
| Data access | Entity Framework Core (SQLite provider) | Local ORM + migrations |
| State management | MVVM (CommunityToolkit.MVVM) | Clean separation of UI and logic |
| Background sync | Background Services | Periodic sync, retry logic, delta uploads |
| Serialization | System.Text.Json | Lightweight JSON for API communication |

### Backend

| Area | Technology | Purpose |
|----------|----------|----------|
| API framework | FastAPI | High‑performance Python backend |
| Database | PostgreSQL | Authoritative datastore for all synced data |
| ORM | SQLAlchemy + Alembic | Models, migrations, schema evolution |
| Auth | JWT (PyJWT) | Secure device authentication | 
| Sync logic | Custom delta‑based sync engine | Handles conflicts, merges, and versioning |
| Deployment | Docker / Docker Compose | Self‑hosted, portable, reproducible |


## Infrastructure

* Docker Compose for local and production deployments
* NGINX (optional) as reverse proxy for SSL termination
* Systemd / Supervisor for backend service management
* Traefik (optional) for dynamic routing in homelab setups

## 🔄 Offline‑First Sync Architecture

WorkLedger uses a delta‑based, queue‑driven sync model:
Client‑Side
* All writes go to the local SQLite database
* Each change is recorded in a sync queue with:
    * Operation type (create/update/delete)
    * Entity type (task/project/log)
    * Local timestamp
    * Version counter
* A background service periodically:
    * Bundles queued changes
    * Sends them to the API
    * Pulls down authoritative updates
    * Resolves conflicts using last‑write‑wins with versioning

## Server‑Side

* FastAPI receives batches of changes
* Each record is compared using:
    * Server version
    * Client version
    * Updated timestamps
* Conflicts are resolved and merged
* Server returns:
    * Updated authoritative records
    * New version numbers
    * Any changes from other devices

## This ensures:

* No data loss
* No overwriting without version checks
* Smooth multi‑device usage

## 📊 Reporting

WorkLedger generates weekly summaries based on:

* Total time per project
* Total time per task
* Daily breakdowns
* Optional export endpoints (JSON/CSV)

Backend endpoints provide:

* `/reports/weekly`
* `/reports/projects/{id}`
* `/reporsts/tasks/{id}`

## 📦 Project Structure

### Client (.NET Avalonia)

```
WorkLedger/
 ├── App.xaml / AppShell.xaml
 ├── Views/
 ├── ViewModels/
 ├── Models/
 ├── Services/
 │    ├── SyncService.cs
 │    ├── AppStateService.cs
 │    └── LocalDbService.cs
 │    └── TimerService.cs
 ```

### Backend (FastAPI)

```
backend/
 ├── app/
 │    ├── main.py
 │    ├── api/
 │    ├── models/
 │    ├── schemas/
 │    ├── services/
 │    ├── sync/
 │    └── auth/
 ├── alembic/
 ├── docker-compose.yml
 └── requirements.txt
 ```

## DevOps Workflow in GitHub
Use tag prefixes in commits to ensure that Release Please picks up the commits and will appropriately handle adding commits to the PR.
PRs will be merged pretty much whenever I think one is ready. 
Conventional Commits cheat sheet for your team to drive the version bumps:
| Commit prefix | Version bump |
|----------|-----------|
| fix:  | v1.0.X patch |
| feat: | v1.X.0 minor |
| feat!: or BREAKING CHANGE: | vX.0.0 major |
|chore:, docs:, refactor: | no bump |

## 🚀 Roadmap

* Mobile‑optimized UI for Android/iOS
* Tagging system for tasks
* Client‑side analytics (focus time, context switching)
* Optional cloud‑sync mode (Supabase or Azure)
* Team mode (shared projects, permissions)

## 📄 License

TBD — MIT, Apache 2.0, or proprietary depending on deployment strategy.
