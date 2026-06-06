# Copilot instructions for WorkLedger-Client

This file gives focused, repo-specific guidance for future Copilot sessions so they can provide accurate, actionable suggestions.

---

## 1) Build, test, and packaging commands
- Restore: `dotnet restore`
- Build: `dotnet build` (CI: `dotnet build --configuration Release --no-restore`)
- Run: `dotnet run`
- Test (all): `dotnet test` (CI runs `dotnet test --configuration Release --no-build`)
- Single test example: `dotnet test --filter "FullyQualifiedName=Namespace.ClassName.MethodName"` or `dotnet test --filter "Name=MyTestMethod"`
- Packaging (used in CI): `dotnet publish -c Release -r <RID> --self-contained true -p:PublishSingleFile=true -o ./publish/<rid>` (RIDs used in CI: win-x64, linux-x64, osx-x64)

Notes: No repo-level lint command found; no test projects detected in the workspace root (create test projects under a Tests/ folder when adding tests).

---

## 2) High-level architecture (big picture)
- Client: .NET 10 Avalonia app (WorkLedger-Client.csproj). UI is MVVM using CommunityToolkit.Mvvm; Views live in `Views/`, VM logic in `ViewModels/`, models in `Models/` and services in `Services/`.
- Local persistence: sqlite-net-pcl (`SQLiteAsyncConnection`) via `Services/LocalDbService.cs`. The app is offline-first and keeps a `workledger.db` by default.
- Sync/Backend: placeholders for `Sync/`, `Auth/` and a planned FastAPI backend described in README. The client maintains a SyncQueue model and background services for orchestration.
- Packaging/Release: GitHub Actions build matrix (windows, ubuntu, macos) for CI, and a package workflow that publishes self-contained builds and creates installers/artifacts.

Files consulted: `README.md`, `CONTRIBUTING.md`, `.github/workflows/*.yml`, `WorkLedger-Client.csproj`, `Services/LocalDbService.cs`, `ViewModels/*`.

---

## 3) Key conventions (repo-specific patterns to respect)
- MVVM pattern: ViewModels inherit `ViewModelBase` (which extends `ObservableObject`). Use `CommunityToolkit.Mvvm` attributes:
  - `[ObservableProperty]` for backing fields (see `MainWindowViewModel`) — Copilot should prefer generating these attributes when scaffolding VMs.
  - `[RelayCommand]` for commands.
- Database access: prefer `LocalDbService` helpers (SaveProjectAsync, SaveTaskAsync, SaveTimeEntryAsync, GetProjectsAsync, etc.) instead of direct SQL. The `LocalDbService` uses `SQLiteAsyncConnection` and creates tables on startup.
- App state and services: `TimerService`, `AppStateService`, and `ThemeService` are singletons or long-lived services wired into VMs. When generating service injection, mirror existing constructors (some VMs instantiate `LocalDbService("workledger.db")` by default; use dependency injection-friendly overloads where present).
- Concurrency: DB API is async (Task-returning). Generated code should prefer `async/await` and avoid blocking calls.
- UI code: XAML lives in `Views/` with minimal code-behind; keep UI-only logic in code-behind and business logic in ViewModels.
- Assets: themes and fonts are included as AvaloniaResources (see `Assets/themes/` and `Assets/fonts/`). When modifying themes, prefer editing `Assets/themes/*.xaml` and follow THEME_USAGE_GUIDE.md.
- Packaging: CI publishes self-contained single-file builds per RID and uses simple platform packaging scripts (NSIS for Windows, dpkg/rpm flows for Linux, DMG for macOS). Keep publish commands consistent with existing workflows.
- Versioning/release: repo uses Conventional Commits + Release Please (see CONTRIBUTING). When making commits for release bumps, use Conventional Commit prefixes.

---

## 4) What to watch for when editing
- The project target is `net10.0` (WorkLedger-Client.csproj). Keep package references aligned with that target.
- `sqlite-net-pcl` is the active DB library in the csproj — avoid generating Entity Framework Core code unless migrating explicitly.
- Many helper methods (LocalDbService) assume `Id == 0` means new entity; preserve that convention.
- UI interactions frequently assume `DateTime.UtcNow` for timestamps and store durations as seconds; keep timezone handling consistent.

---

## 5) Helpful quick pointers for Copilot-style suggestions
- Prefer adding `async` methods that return `Task`/`Task<T>` and follow existing naming (GetXAsync, SaveXAsync).
- When generating ViewModel properties, use `[ObservableProperty]` and named backing fields to match existing style.
- When editing or adding persistence, use `LocalDbService` methods rather than directly manipulating `SQLiteAsyncConnection` unless a new DB feature is needed.
- For any change that affects DB schema or migrations, add clear notes in PR and update the README/CONTRIBUTING if workflows change.

---

(Generated from repository files: README.md, CONTRIBUTING.md, .github workflows, csproj and key source files.)
