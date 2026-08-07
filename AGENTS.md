# AGENTS.md

Guidance for running multiple coding agents (Claude Code subagents, parallel sessions, or any other
agentic tool) against the sync/versioning/reports build-out described in `WorkLedger_MVP_Plan.md` and
`plans/`. Read `CLAUDE.md` first for architecture and conventions — this file is only about **how to split
and parallelize** the 7 plans in `plans/` safely.

## The plans, and what actually depends on what

```
1 (housekeeping)
      │
      ▼
2 (data model versioning)
      │
      ├──────────────┐
      ▼              ▼
3 (sync queue)   4 (sync abstraction + orchestrator)      ← 3 and 4 can run in PARALLEL
      │              │
      └──────┬───────┘
             ▼
     ┌───────┴───────┐
     ▼               ▼
5 (file provider) 6 (api provider)                        ← 5 and 6 can run in PARALLEL
     │               │
     └───────┬───────┘
             ▼
    7 (sync + reports UI, release)
```

- **1 and 2 are hard prerequisites** for everything else — never parallelize against them. 1 has to land
  first because it adds the test project every other plan writes tests into; 2 has to land second because
  every later plan reads/writes `Version`/`OriginDeviceId`/`DeviceId`.
- **3 and 4 are safe to run at the same time** once 2 is merged — see the file-overlap note below for the
  one thing to watch.
- **5 and 6 are safe to run at the same time** once 4 is merged, and don't depend on each other at all.
- **7 always runs last**, solo — it's the integration point that wires everything the parallel groups built.

Only two agents running concurrently at any point is the realistic ceiling here (one parallel group at a
time) — there's no benefit to over-parallelizing plans that are already sequential dependencies of each
other.

## Where parallel agents will actually touch the same file

Running 3 and 4 (or 5 and 6) in separate git worktrees avoids *branch* collisions, but they can still
produce a *merge* conflict if both edit the same file. Know these ahead of time:

- **3 vs. 4** — Plan 3 edits `Services/LocalDbService.cs`'s `SaveXAsync`/`DeleteXAsync` methods directly (to
  enqueue `SyncQueue` rows). Plan 4's `SyncOrchestrator` only needs to *read* `SyncQueue` rows and mark them
  synced — it should do this via the already-public `LocalDbService.Connection` property
  (`Services/LocalDbService.cs:122`, e.g. `connection.Table<SyncQueue>().Where(q => !q.IsSynced)...`) or the
  existing generic `GetAllAsync<T>`/`UpdateAsync<T>` helpers, **not** by adding new bespoke query methods to
  `LocalDbService` itself. If the agent working Plan 4 is tempted to add a `GetUnsyncedQueueAsync()` method
  to `LocalDbService`, that's the one thing likely to collide with Plan 3's edits to the same file — steer it
  toward the generic helpers instead.
- **5 vs. 6** — Both add new fields to `AppSettings` and a matching backfill check in
  `SettingsService.LoadSettings()` (`Services/SettingsService.cs`) — Plan 5 adds `SyncFolderPath`, Plan 6
  adds `ApiEndpoint`/`ApiToken`. These are almost certainly non-overlapping *lines* if both agents follow the
  existing `BackupDirectory` pattern (append a new field to the class, append a new independent backfill `if`
  block rather than editing an existing one) — but it's still two agents editing the same file, so expect a
  trivial merge/rebase step, not a silent auto-merge.
- Neither parallel group touches `Views/MainWindow.axaml` or `App.axaml.cs` — those are reserved for Plan 7
  (and Plan 2, solo, for `App.axaml.cs`'s `DeviceId` wiring), so no XAML conflicts should occur during
  parallel phases.

## Isolation protocol

Give each concurrently-running agent its own git worktree and branch, cut from the same up-to-date `main`
(never from each other):

```bash
git worktree add ../wl-49-sync-queue      -b wl-49-sync-queue-wiring       main
git worktree add ../wl-50-sync-orchestrator -b wl-50-sync-orchestrator     main
```

If you're driving this from within a single Claude Code session, the `Agent` tool's
`isolation: "worktree"` option does this automatically per subagent — launch the Plan 3 and Plan 4 agents in
the **same message** (multiple tool calls) so they actually run concurrently rather than one blocking the
other. Each agent should be briefed with:

1. The full contents of that plan's file under `plans/` (its Context/Scope/Out-of-scope/Acceptance
   criteria) — the plan file's own "Prompt for Claude Code" section at the bottom is the right thing to hand
   over verbatim as the task description.
2. A pointer to `CLAUDE.md` for architecture/conventions.
3. An explicit instruction to stay inside that plan's scope — don't let a Plan 3 agent start implementing
   `SyncOrchestrator`, or vice versa, even if it seems like "just a little bit" of the other plan.

## Merge protocol

Parallel work still merges **sequentially**, not simultaneously:

1. Whichever of the pair finishes and passes review first, merge it to `main`.
2. Rebase the second branch onto the now-updated `main` (this is where the small `SettingsService.cs` /
   `LocalDbService.cs` overlaps noted above get resolved — they should be trivial with the guidance followed,
   but don't skip actually looking at the diff).
3. Run `dotnet build` and `dotnet test` after the rebase and again after the second merge — don't assume a
   clean rebase means the combined result still builds; the two branches were developed without seeing each
   other's changes.
4. Only start the next milestone (or the next parallel pair) once both branches in the current group are
   merged and green.

## Non-goals

- Don't parallelize 1 with anything — nothing else has a test project to write tests into until it lands.
- Don't parallelize 2 with anything — every later plan depends on the fields it adds.
- Don't start 7 until 4 and 5 are merged (6 only if you want the API-mode config fields in the same pass;
  otherwise 7's API section can follow in a quick follow-up once 6 lands).
- Don't invent a third parallel lane by splitting a single plan file in half — each plan file was scoped
  specifically to be one coherent, reviewable unit of work; splitting further just adds coordination
  overhead for no real speedup given how small each one is.
