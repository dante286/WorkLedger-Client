# Plan 2 — Data Model Versioning (ticket TBD — see note in README.md)

## Context

README.md describes the sync model as last-write-wins with a version counter (`README.md:169`, `:180-182`),
but today's models only have `UpdatedAt`:

```csharp
// Models/Project.cs
public class Project {
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public string? Name { get; set; }
    public string? ProjectCode { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
```

`TaskItem` and `TimeEntry` follow the same shape (`Models/TaskItem.cs`, `Models/TimeEntry.cs`) — no `Version`,
no per-device origin identifier. Sync (Plans 3–6) cannot resolve conflicts correctly without both: `Version`
lets last-write-wins compare "how many edits has this record seen" rather than trusting clock skew alone, and
`OriginDeviceId` lets the orchestrator tell "did this change come from me or from a remote peer" so it doesn't
re-apply its own writes as if they were incoming.

`Services/LocalDbService.cs` creates tables via `sqlite-net-pcl`'s `CreateTableAsync<T>()` in
`InitializeAsync()` (`Services/LocalDbService.cs:20-26`). **`sqlite-net-pcl`'s `CreateTableAsync` does add
missing columns to an existing table automatically** (it runs `ALTER TABLE ... ADD COLUMN` for any mapped
property not already present) — so simply adding properties to the model classes and letting `CreateTableAsync`
run again on next launch already handles the schema side for existing SQLite files. What it does *not* do is
backfill sensible values into the new columns for pre-existing rows (new columns come back as `0`/`NULL`), so
this plan still needs an explicit backfill step, plus a place to mint and persist a stable per-install device ID.

## Scope

1. **Add `Version` (int) to `Project`, `TaskItem`, `TimeEntry`.**
   - Default `1` for newly-created rows (set in each model's default initializer, mirroring the existing
     `CreatedAt` default pattern).
   - This is a plain persisted int column — no `[Indexed]` needed (it's never queried by directly, only read
     alongside a row it belongs to).
2. **Add `OriginDeviceId` (string) to `Project`, `TaskItem`, `TimeEntry`.**
   - This identifies which installation last wrote the row — required for the orchestrator (Plan 4) to
     distinguish local vs. remote origin without a separate lookup.
3. **Mint and persist a per-install device ID.**
   - Add `DeviceId` (string, GUID, generated once and persisted) to `AppSettings` in
     `Services/SettingsService.cs` (`AppSettings` class at line 9), following the same "load-or-default"
     pattern already used for `DataDirectory`/`ApplicationVersion` backfill in `LoadSettings()`
     (`Services/SettingsService.cs:118-129`): if `DeviceId` is empty on load, generate `Guid.NewGuid()`,
     assign, and `SaveSettings()` immediately so it's stable across restarts.
   - Expose it so `LocalDbService`/callers can stamp it onto `OriginDeviceId` on every save — the cleanest
     approach given `LocalDbService`'s constructor already takes just a `dbPath` string
     (`Services/LocalDbService.cs:12`) is to add an optional constructor parameter or a settable property
     (e.g. `LocalDbService.DeviceId`) that `App.axaml.cs` populates from `SettingsService` right after
     construction, before `MainWindowViewModel` starts using it. Don't thread `SettingsService` itself into
     `LocalDbService` — pass just the resolved ID string to keep the dependency one-directional.
4. **Increment `Version` on update.** In `LocalDbService.SaveProjectAsync` / `SaveTaskAsync` /
   `SaveTimeEntryAsync` (`Services/LocalDbService.cs:62-68`, `:85-91`, `:107-113`), the existing
   insert-vs-update branch (`if (entity.Id == 0) Insert else Update`) is exactly where to hook in:
   - On insert: set `Version = 1` (if not already defaulted by the model) and `OriginDeviceId = DeviceId`.
   - On update: increment `Version` (read-modify-write is fine since these are already single-record calls)
     and set `OriginDeviceId = DeviceId` before calling `UpdateAsync`.
   - Keep the existing method signatures — this is an internal change, not an API change.
5. **Schema migration for existing databases.** Two parts:
   - **Columns**: confirmed above that `CreateTableAsync<T>()` already adds them on next startup — verify
     this experimentally against a pre-existing `workledger.db` fixture rather than assuming, since behavior
     can vary by `sqlite-net-pcl` version (this repo pins `1.9.172`, `WorkLedger-Client.csproj:30`).
   - **Backfill**: after `CreateTableAsync` runs for each entity in `InitializeAsync()`
     (`Services/LocalDbService.cs:20-26`), add a one-time backfill step: for any row where `Version == 0`
     (the zero-value default for an `int` column that didn't exist before), set `Version = 1` and
     `OriginDeviceId = DeviceId` (best-effort attribution — pre-existing rows have no real origin history, so
     stamping the current device is the only sane default) via a batch `UPDATE`. Guard this so it only runs
     once — e.g. check whether any row has `Version == 0` before bothering to scan the whole table, so this
     doesn't add overhead to fresh installs or already-migrated databases on every future startup.
6. **Tests** (in the `Tests/WorkLedger.Tests` project from Plan 1):
   - New inserts get `Version == 1` and the correct `OriginDeviceId`.
   - An update increments `Version` by exactly 1 and refreshes `OriginDeviceId`.
   - Migration backfill: seed a temp SQLite file with rows inserted *before* the new columns exist (or
     simulate by inserting via raw SQL without the new columns, then run `InitializeAsync`), confirm
     `Version` becomes `1` and `OriginDeviceId` becomes the current device ID, and confirm running
     `InitializeAsync` a second time doesn't re-touch already-migrated rows (e.g. doesn't bump `Version` to 2).

## Out of scope

- No sync queue writes yet (that's Plan 3) — this plan only adds the columns and versioning behavior that
  the sync queue and orchestrator will later depend on.
- No UI changes — `Version`/`OriginDeviceId`/`DeviceId` are not user-facing in this plan.
- Don't touch `TaskItemViewModel`'s direct reach into `_localDbService` (noted as a known inconsistency in
  `CLAUDE.md`) unless it's directly in the way of this change.

## Acceptance criteria

- `Project`, `TaskItem`, `TimeEntry` each have `Version` (int, default 1) and `OriginDeviceId` (string).
- A fresh install's first run produces rows with `Version == 1` and a real `OriginDeviceId`.
- Updating an existing row increments `Version` and refreshes `OriginDeviceId` to the current device.
- Loading a pre-existing (pre-this-change) `workledger.db` file does not crash, and its rows get backfilled
  to `Version = 1` exactly once.
- `AppSettings.DeviceId` is generated once and stable across app restarts (verify by loading settings twice
  and confirming the same ID both times).
- All new behavior has passing unit tests in `Tests/WorkLedger.Tests`.

## Suggested commit message

```
feat(sync): add Version and OriginDeviceId to core models with migration
```

## Prompt for Claude Code

> Add `Version` (int, default 1) and `OriginDeviceId` (string) columns to `Project`, `TaskItem`, and
> `TimeEntry` in `Models/`. Add a `DeviceId` (GUID string, generated once and persisted) to `AppSettings` in
> `SettingsService`, following the existing backfill pattern in `LoadSettings()`. Update
> `LocalDbService.SaveProjectAsync`/`SaveTaskAsync`/`SaveTimeEntryAsync` so inserts set `Version = 1` and
> `OriginDeviceId`, and updates increment `Version` and refresh `OriginDeviceId`. Add a safe one-time
> migration/backfill step in `InitializeAsync` for existing SQLite files whose rows predate these columns
> (sqlite-net-pcl's `CreateTableAsync` already adds missing columns — verify that, then backfill `Version = 1`
> and `OriginDeviceId` for any row where `Version == 0`, guarded so it only runs once). Write unit tests for
> the insert/update versioning behavior and the migration backfill.
