# Plan 3 — Wire Up the Sync Queue (ticket TBD — see note in README.md)

## Context

`Models/SyncQueue.cs` already exists and is well-shaped for this:

```csharp
public class SyncQueue {
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public string? EntityType { get; set; }   // "project", "task", "timeentry"
    public int EntityId { get; set; }
    public string? Operation { get; set; }               // "create", "update", "delete"
    public string? PayloadJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsSynced { get; set; } = false;
}
```

Its table is already created in `LocalDbService.InitializeAsync()` (`Services/LocalDbService.cs:25`), but
**nothing ever inserts a row into it**. `WorkLedger_MVP_Plan.md` calls this out as the single biggest gap
between "local-only app" and the stated sync architecture. This plan is purely mechanical: every existing
create/update/delete path in `LocalDbService` needs to also write a matching `SyncQueue` row.

This plan depends on Plan 2 (`Version`/`OriginDeviceId` on the entities) since the queued `PayloadJson` should
capture the entity's state *after* those fields are stamped — queuing a payload without its version would
make the orchestrator (Plan 4) unable to do last-write-wins comparison later.

The methods to touch, all in `Services/LocalDbService.cs`:

| Entity | Save | Delete |
|---|---|---|
| Project | `SaveProjectAsync` (`:62-68`) | `DeleteProjectAsync` (`:70-73`) |
| TaskItem | `SaveTaskAsync` (`:85-91`) | `DeleteTaskAsync` (`:93-96`) |
| TimeEntry | `SaveTimeEntryAsync` (`:107-113`) | `DeleteTimeEntryAsync` (`:115-118`) |

## Scope

1. **Add a private helper** in `LocalDbService`, e.g.:
   ```csharp
   private Task EnqueueSyncAsync(string entityType, int entityId, string operation, object? payload)
   {
       var entry = new SyncQueue
       {
           EntityType = entityType,
           EntityId = entityId,
           Operation = operation,
           PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload),
           IsSynced = false
       };
       return _db.InsertAsync(entry);
   }
   ```
   Use `System.Text.Json` (already used elsewhere in this codebase, e.g. `SettingsService`) rather than
   introducing a new serialization dependency.
2. **Call it from every Save/Delete method**, after the underlying insert/update/delete succeeds:
   - `SaveProjectAsync`: `"create"` when `project.Id == 0` (pre-insert — capture `EntityId` from the value
     *after* `InsertAsync` completes and `Id` is populated via autoincrement), `"update"` otherwise.
   - `SaveTaskAsync` / `SaveTimeEntryAsync`: same pattern, `EntityType` = `"task"` / `"timeentry"` (match
     whatever casing/naming convention gets picked — just be consistent, since Plan 4's orchestrator will
     switch on these strings).
   - `DeleteProjectAsync` / `DeleteTaskAsync` / `DeleteTimeEntryAsync`: `"delete"`, payload can be the
     already-deleted entity (id + whatever fields matter for a remote tombstone) or just `null` with
     `EntityId` set — decide based on what Plan 4's merge logic will need to reconcile a delete against a
     remote update (a null payload can't tell "this row doesn't exist" apart from "no data available"; a
     minimal payload with `Id`, `Version`, `OriginDeviceId` is safer and cheap to include).
3. **Same-transaction where possible.** `sqlite-net-pcl`'s `SQLiteAsyncConnection` supports
   `RunInTransactionAsync` for synchronous callback bodies; since the existing insert/update calls are simple
   single-statement operations, wrap the entity write + the `SyncQueue` insert together so a crash between
   the two can't leave an entity change un-queued. If `RunInTransactionAsync`'s synchronous-delegate signature
   makes wrapping two async calls awkward with this SQLite provider, it's acceptable to do both calls
   sequentially without a literal DB transaction *as long as it's clearly noted as a follow-up* — don't
   silently skip the "same transaction" requirement without flagging it.
4. **Tests**: for each of the 6 methods (3 entities × save/delete), confirm exactly one new `SyncQueue` row is
   created with the correct `EntityType`/`Operation`/`EntityId`, and that `SaveXAsync`'s insert-vs-update
   branch produces `"create"` vs `"update"` correctly.

## Out of scope

- Don't build anything that *reads* or *drains* the queue yet — that's `SyncOrchestrator` in Plan 4. This
  plan is write-only.
- Don't add a `SyncMode` setting or any UI — that's Plan 7.
- Don't touch the generic `InsertAsync<T>`/`UpdateAsync<T>`/`DeleteAsync<T>` helpers
  (`Services/LocalDbService.cs:29-39`) — those are generic passthroughs used elsewhere; only the
  entity-specific methods need queuing since only they represent actual user-facing mutations today.

## Acceptance criteria

- Every create, update, and delete of `Project`, `TaskItem`, `TimeEntry` produces exactly one new `SyncQueue`
  row with `IsSynced = false`, correct `EntityType`, `EntityId`, `Operation`, and a `PayloadJson` sufficient
  to reconstruct or reconcile the change.
- Existing callers of these methods (`MainWindowViewModel`, `TaskItemViewModel`) require no changes — this is
  purely additive inside `LocalDbService`.
- Unit tests cover all 6 operation paths and pass.

## Suggested commit message

```
feat(sync): enqueue SyncQueue entries on every entity mutation
```

## Prompt for Claude Code

> Modify `LocalDbService` so every create/update/delete of `Project`, `TaskItem`, and `TimeEntry` also writes
> a corresponding `SyncQueue` entry (`EntityType`, `EntityId`, `Operation` = create/update/delete,
> `PayloadJson` = serialized entity state via `System.Text.Json`, `IsSynced = false`). Add a private
> `EnqueueSyncAsync` helper and call it from `SaveProjectAsync`, `SaveTaskAsync`, `SaveTimeEntryAsync`,
> `DeleteProjectAsync`, `DeleteTaskAsync`, `DeleteTimeEntryAsync` in `Services/LocalDbService.cs`. Do the
> entity write and the queue insert in the same transaction where `sqlite-net-pcl` makes that practical. Add
> tests confirming a queue row is created for each of the 6 operation paths with the correct fields.
