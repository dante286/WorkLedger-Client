# Plan 4 — Sync Abstraction + Orchestrator (ticket TBD — see note in README.md)

## Context

`Sync/` exists today only as an empty placeholder folder referenced in the `.csproj`
(`WorkLedger-Client.csproj:14`, `<Folder Include="Sync\" />`). This plan gives it real content: the
`ISyncProvider` contract plus a `SyncOrchestrator` that drains the `SyncQueue` (now being populated as of
Plan 3) and applies last-write-wins merges using the `Version`/`OriginDeviceId` fields (added in Plan 2).

This plan intentionally does **not** implement a real transport — no file provider, no API calls. It builds
the shape everything else plugs into, stubbed with a no-op provider, so Plans 5 and 6 can each implement one
concrete `ISyncProvider` in isolation without needing to also design the merge logic.

Two existing patterns in this codebase to mirror:
- **Periodic background work**: `Services/TimerService.cs` already runs a `System.Timers.Timer` for the
  backup tick (`_backupTimer`, `ConfigureBackupInterval`, `BackupTick` event, `RunOnCloseBackupAsync` for a
  bounded on-close variant, lines 14/27-28/110-153). `SyncOrchestrator` should follow the same shape: a timer,
  a `ConfigureSyncInterval`-style method, and an event or direct method invoked periodically — not a new
  concurrency pattern.
- **Startup wiring**: `App.axaml.cs` constructs services in a fixed order (`localDb` →`appState` →
  `timerService` → `themeService` → `toastService` → `mainViewModel`, `App.axaml.cs:62-78`) and wires
  `desktop.Exit` for a bounded on-close action (`:86-99`). `SyncOrchestrator` construction and any on-close
  sync flush should slot into this same sequence, not introduce a separate bootstrapping path.

## Scope

1. **`Sync/ISyncProvider.cs`** — the contract:
   ```csharp
   public interface ISyncProvider
   {
       Task<SyncResult> PushAsync(IEnumerable<SyncQueue> pendingChanges);
       Task<IEnumerable<RemoteChange>> PullAsync(DateTime since);
   }
   ```
2. **`Sync/SyncResult.cs`** — outcome of a push: at minimum, which `SyncQueue.Id`s were successfully
   accepted (so the orchestrator knows which to mark `IsSynced = true`), and any failure detail for a future
   retry/backoff policy (out of scope to *implement* retry here, but the shape should allow for it later).
3. **`Sync/RemoteChange.cs`** — a change pulled from a remote source: `EntityType`, `EntityId`,
   `Operation`, `PayloadJson` (mirroring `SyncQueue`'s shape, since it's describing the same kind of event
   from a different origin), plus `Version` and `OriginDeviceId` extracted from (or alongside) the payload so
   the orchestrator can compare versions without re-deserializing per entity type in the merge step.
4. **`Sync/NullSyncProvider.cs`** — a no-op `ISyncProvider`: `PushAsync` returns success with nothing
   applied, `PullAsync` returns an empty list. This is the default/placeholder until `SyncMode` is wired to a
   real provider in Plan 7.
5. **`Services/SyncOrchestrator.cs`** — the orchestrator:
   - Constructor takes `LocalDbService` and the active `ISyncProvider` (start with `NullSyncProvider`; Plan 7
     is what makes this swappable via a `SyncMode` setting — don't build that switch here, just make the
     orchestrator accept whatever provider it's given).
   - `RunSyncAsync()`: reads unsynced `SyncQueue` rows (`WHERE IsSynced = false`) from `LocalDbService`,
     calls `provider.PushAsync(pending)`, marks the rows in the returned `SyncResult` as `IsSynced = true`.
   - Then calls `provider.PullAsync(lastSyncedAt)` and applies each `RemoteChange` using **last-write-wins**:
     compare the remote `Version` against the local row's current `Version` for that `EntityType`/`EntityId`;
     if the remote wins (higher version, or same version with a tie-break — pick one and document it, e.g.
     "higher version wins; on tie, the change with a later `CreatedAt` wins; on a full tie, local wins to
     avoid needless writes"), apply it via the existing `SaveXAsync`/`DeleteXAsync` methods so the write
     still stamps a fresh local `Version`/`OriginDeviceId` and (per Plan 3) re-enqueues correctly. If the
     remote change's `OriginDeviceId` matches this device's own `DeviceId`, skip it — it's an echo of a
     change this device already made, not a real incoming update.
   - Follow the `TimerService` shape for periodic invocation: a `System.Timers.Timer`-based
     `ConfigureSyncInterval(...)`, and expose a manual "sync now" entry point (`RunSyncAsync()` itself,
     callable directly) — Plan 7 wires the manual button and the periodic tick into
     `MainWindowViewModel`/`App.axaml.cs`; this plan just needs the orchestrator to support being driven
     either way.
6. **Tests** using fakes: a fake `ISyncProvider` that returns canned `SyncResult`/`RemoteChange` values, and
   an in-memory or temp-file `LocalDbService`, to verify:
   - Pending queue rows get pushed and marked synced on success.
   - A remote change with a higher `Version` overwrites the local row.
   - A remote change with a lower/equal `Version` is discarded (local wins).
   - A remote change whose `OriginDeviceId` equals the local device's own ID is skipped (no self-echo).
   - A remote `"delete"` operation actually removes the local row.

## Out of scope

- No `FileSyncProvider` or `ApiSyncProvider` implementation — those are Plans 5 and 6.
- No `SyncMode` setting, no UI, no automatic wiring of `SyncOrchestrator` into `App.axaml.cs`'s startup phases
  or a "Sync Now" button yet — that's Plan 7. This plan should leave `SyncOrchestrator` fully testable and
  ready to be constructed, but doesn't have to be actually instantiated by the running app yet (a follow-up
  plan wires it in once there's a real provider and a setting to pick one).
- No retry/backoff policy for failed pushes — `SyncResult` should have room for it, but implementing it is
  future work.

## Acceptance criteria

- `Sync/ISyncProvider.cs`, `Sync/SyncResult.cs`, `Sync/RemoteChange.cs`, `Sync/NullSyncProvider.cs` exist and
  compile.
- `Services/SyncOrchestrator.cs` implements push-then-pull-then-merge as described, using
  `Version`/`OriginDeviceId` for conflict resolution.
- The documented last-write-wins tie-break rule is written down (in code comments or this plan's follow-up
  notes) so Plans 5/6/7 don't have to re-derive it.
- Unit tests cover: successful push marking rows synced, remote-wins merge, local-wins merge, self-echo
  skip, and remote delete — all passing.

## Addendum (WL-47/54/56, WL-49/60): SyncId-keyed merges + SyncQueue pruning

Gap review after this plan's first pass (WL-47, WL-54, WL-56) found that keying `RemoteChange.EntityId` /
merge lookups off the local autoincrement `Id` breaks the moment two offline devices independently create a
row that lands on the same local `Id` — the orchestrator would treat them as the same entity and last-write-wins
one away. `RemoteChange.EntityId` is now `Guid` and merge lookups (`ApplyProjectChangeAsync` etc.) match local
rows by `SyncId`, never `Id`. When a `RemoteChange`'s `SyncId` has no local match, the incoming entity's `Id` is
left at `0` so `SaveXAsync` inserts a new row instead of updating a nonexistent one — this fully resolves what
was previously documented here as a known limitation. A regression test
(`SyncOrchestratorTests.RunSyncAsync_TwoDevicesWithCollidingLocalIds_DoNotCollideAfterSync`) covers this
directly, per WL-56.

Separately, WL-49/WL-60 flagged that nothing in Plans 3 or 4 ever prunes `SyncQueue` rows once
`IsSynced = true`, so the table would grow unbounded for the life of an install. `RunSyncAsync` now deletes
already-synced rows at the end of every cycle (`PruneSyncedQueueAsync`) — since it runs periodically via
`ConfigureSyncInterval`, this satisfies "periodically prune" without a separate schedule. WL-49's other half
(unbounded `<syncFolder>/changes/*.json` growth) is `FileSyncProvider`'s concern — see the addendum in
`plans/05-file-sync-provider.md`.

## Suggested commit message

```
feat(sync): add ISyncProvider abstraction and SyncOrchestrator with last-write-wins merge
```

## Prompt for Claude Code

> Create `Sync/ISyncProvider.cs` defining the sync provider contract (`PushAsync`/`PullAsync`), plus
> `Sync/SyncResult.cs` and `Sync/RemoteChange.cs` for the push outcome and pulled-change shapes. Add
> `Sync/NullSyncProvider.cs` as a no-op implementation. Create `Services/SyncOrchestrator.cs` implementing
> push/pull/merge using `SyncQueue` and the `Version`/`OriginDeviceId` fields added in the prior data-model
> plan, with last-write-wins conflict resolution (higher `Version` wins; document the tie-break rule you
> choose) and skip-on-self-echo when a remote change's `OriginDeviceId` matches the local device. Follow the
> periodic-timer pattern already used in `Services/TimerService.cs` for the backup tick. Don't implement a
> concrete provider yet. Add unit tests for the merge logic using a fake `ISyncProvider` and a temp SQLite
> `LocalDbService`.
