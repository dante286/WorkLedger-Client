# Plan 5 — File-Based Sync Provider (ticket TBD — see note in README.md)

## Context

This is the plan that delivers actual user-facing sync value with **zero backend dependency** — per
`WorkLedger_MVP_Plan.md`'s sequencing notes, prioritize this over Plan 6 (API provider) if time is
constrained, since two machines pointed at the same Dropbox/OneDrive/network-share folder can already merge
changes through this alone.

Depends on Plan 4's `ISyncProvider` contract and `RemoteChange`/`SyncResult` shapes. This plan implements one
concrete provider against that contract — it should not need to change the interface.

Two existing patterns to mirror closely, both in `Services/SettingsService.cs` and
`ViewModels/MainWindowViewModel.cs`:
- **Settings field + folder picker + validation**, exactly like `BackupDirectory`:
  - `AppSettings.BackupDirectory` (`Services/SettingsService.cs:16`) is a plain string field, persisted like
    any other setting.
  - `MainWindowViewModel.PickBackupFolderAsync` (`ViewModels/MainWindowViewModel.cs:698-...`) gets the
    desktop lifetime, resolves `TopLevel.GetTopLevel(desktop.MainWindow)`, calls
    `topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false })`,
    and assigns the picked path — `SyncFolderPath` should get an equivalent `PickSyncFolderCommand` following
    the identical shape.
  - `SettingsService.ValidateBackupSource` (`Services/SettingsService.cs:227-240`) checks a folder "looks
    like" a valid backup source before trusting it — `FileSyncProvider` needs an analogous check (e.g. does
    the folder exist / is it writable) before treating a configured `SyncFolderPath` as usable.

## Scope

1. **`Sync/FileSyncProvider.cs`** implementing `ISyncProvider`:
   - Constructor takes the configured sync folder path (and this device's `DeviceId` from `AppSettings`,
     Plan 2) — don't reach into `SettingsService` directly from inside `Sync/`; pass resolved values in,
     matching how `LocalDbService` stays decoupled from `SettingsService` today.
   - `PushAsync(pendingChanges)`: serialize the batch of pending `SyncQueue` rows into one JSON file per push,
     named so it sorts and identifies its origin unambiguously, e.g.
     `<syncFolder>/changes/<deviceId>-<timestampUtc:yyyyMMddHHmmssfff>.json`. Contents: an array of
     `{ EntityType, EntityId, Operation, PayloadJson, Version, OriginDeviceId, CreatedAt }` — reuse the
     `SyncQueue`/`RemoteChange` shape rather than inventing a new wire format. Return a `SyncResult` marking
     all pushed rows as accepted once the file write succeeds (a local file write either fully succeeds or
     throws — there's no partial-acceptance case to model here, unlike a real network call).
   - `PullAsync(since)`: list `<syncFolder>/changes/*.json`, filter out files whose `deviceId` prefix matches
     this device's own `DeviceId` (a device never needs to pull its own pushes back), filter to files with a
     timestamp after `since` (or track "already-processed filenames" instead of relying purely on timestamp,
     to avoid re-processing a file whose name sorts oddly — pick one and be consistent), parse each into
     `RemoteChange` records, and return them.
   - Handle the missing-folder / permission-denied case gracefully — mirror
     `SettingsService.TryCopyFileWithRetriesAsync`'s retry pattern (`Services/SettingsService.cs:295-317`) or
     at minimum catch and surface a clear error rather than throwing an unhandled exception up through
     `SyncOrchestrator`.
2. **`SyncFolderPath` setting**, added to `AppSettings` in `Services/SettingsService.cs`, following the exact
   pattern `BackupDirectory` already uses (default empty string, backfilled on load if missing — see the
   `BackupDirectory` backfill at `Services/SettingsService.cs:132-136` for the shape to copy).
3. **Folder-picker command** in `MainWindowViewModel`: `PickSyncFolderCommand`, cloned from
   `PickBackupFolderAsync` (`ViewModels/MainWindowViewModel.cs:698-...`), assigning to a new
   `SyncFolderPath` bindable property (this plan only needs the command + property to exist for Plan 7 to
   bind to in the UI — the actual Settings-tab XAML section is Plan 7's job, not this one).
4. **Folder validation**: a `FileSyncProvider`-adjacent helper (or a method on `SettingsService`, matching
   where `ValidateBackupSource` lives) confirming the configured folder exists/is creatable before the
   orchestrator tries to use it.
5. **Tests**:
   - `PushAsync` writes a JSON file with the expected name pattern and contents for a batch of fake
     `SyncQueue` rows.
   - `PullAsync` reads back changes written by a *different* device ID and returns them as `RemoteChange`s.
   - `PullAsync` does **not** return a device's own pushed changes (self-filter works).
   - Two devices' conflicting changes to the same entity (same `EntityType`/`EntityId`, different
     `OriginDeviceId`, different `Version`) both show up from `PullAsync` as distinct `RemoteChange`s — the
     *resolution* of that conflict is `SyncOrchestrator`'s job (Plan 4), not this provider's, so the test here
     just confirms both are surfaced, not silently deduplicated.
   - Pointing at a nonexistent/unwritable folder fails gracefully (no unhandled exception).

## Out of scope

- No Settings-tab UI section — Plan 7 adds the XAML. This plan only needs the ViewModel command/property to
  exist so Plan 7 has something to bind to.
- No automatic cleanup/pruning of old change files in the sync folder — note this as a known limitation
  rather than solving it here (an unbounded folder of JSON files is a real future concern, just not this
  plan's scope).
- No conflict resolution logic — that's already built in `SyncOrchestrator` (Plan 4); this provider only
  needs to faithfully push/pull.

## Acceptance criteria

- `Sync/FileSyncProvider.cs` implements `ISyncProvider` fully against a real folder on disk.
- `AppSettings.SyncFolderPath` exists, persists, and follows the `BackupDirectory` backfill pattern.
- `MainWindowViewModel.PickSyncFolderCommand` exists and sets `SyncFolderPath` via the same folder-picker flow
  as `PickBackupFolderCommand`.
- Two `LocalDbService`/`FileSyncProvider` instances pointed at the same temp folder, simulating two devices,
  can push their own changes and pull each other's via the provider (end-to-end test, not just isolated
  unit tests) — this is the concrete proof that the "sync via shared folder" story from
  `WorkLedger_MVP_Plan.md` actually works.
- All new provider logic has passing unit tests, including the graceful-failure case.

## Suggested commit message

```
feat(sync): implement FileSyncProvider for folder-based sync
```

## Prompt for Claude Code

> Implement `Sync/FileSyncProvider.cs` per the `ISyncProvider` contract from the sync-abstraction plan:
> `PushAsync` writes pending `SyncQueue` changes as a JSON file into a configured sync folder (named
> `<deviceId>-<timestamp>.json`), `PullAsync` reads other devices' files (excluding this device's own) and
> returns unprocessed changes as `RemoteChange`s. Add `SyncFolderPath` to `AppSettings`/`SettingsService`
> mirroring exactly how `BackupDirectory` already works (including a `PickSyncFolderCommand` on
> `MainWindowViewModel` cloned from `PickBackupFolderAsync`). Write tests covering push, pull, the
> self-exclusion filter, and two devices producing conflicting changes to the same entity (both should
> surface from `PullAsync`, not be silently merged — that's the orchestrator's job). Include a graceful-failure
> path when the configured folder doesn't exist or isn't writable.
