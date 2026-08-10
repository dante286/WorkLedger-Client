# WorkLedger Build Plans — Index

Derived from [`WorkLedger_MVP_Plan.md`](../WorkLedger_MVP_Plan.md) (the M0–M7 overview). Each file in this
folder is a **self-contained** plan meant to be handed to a Claude Code session as its own unit of work —
its own branch, its own PR, its own review. M7 (Release) is small enough that it's folded into the M6 plan
rather than getting its own file, so there are 7 plans covering the 8 original milestones.

> **Ticket numbers note:** these plans originally referenced WL-47 through WL-54 as their tickets, but those
> tickets were never actually created in Jira — the numbers were placeholders. That range has since been
> claimed by unrelated tickets (WL-47–WL-72: gap tickets covering global sync identifiers, retry/backoff,
> sync artifact retention, reports export, pre-merge safety backups, and MainWindowViewModel/TaskItemViewModel
> cleanup, plus subtasks added to WL-9). When picking up one of the 7 plans below, create its ticket fresh
> (it'll get whatever the next available WL-## number is) and update this table with the real number.

| # | File | Ticket(s) | Depends on |
|---|------|-----------|------------|
| 1 | [01-housekeeping.md](01-housekeeping.md) | TBD | — |
| 2 | [02-data-model-versioning.md](02-data-model-versioning.md) | WL-73 | 1 |
| 3 | [03-sync-queue-wiring.md](03-sync-queue-wiring.md) | TBD | 2 |
| 4 | [04-sync-abstraction-orchestrator.md](04-sync-abstraction-orchestrator.md) | TBD | 2 (parallel with 3) |
| 5 | [05-file-sync-provider.md](05-file-sync-provider.md) | TBD | 4 |
| 6 | [06-api-sync-provider.md](06-api-sync-provider.md) | TBD | 4 |
| 7 | [07-sync-reports-ui-and-release.md](07-sync-reports-ui-and-release.md) | TBD | 4, 5 |

## Sequencing

- **1 and 2 are prerequisites for everything else** — run them first, in order.
- **3 and 4 can run in parallel** once 2 lands (different files: 3 touches `LocalDbService`, 4 adds a new `Sync/` abstraction + `SyncOrchestrator`).
- **5 and 6 both depend on 4** but not on each other. **5 (file-based) delivers real user value with zero backend dependency — prioritize it over 6** if time is constrained. 6 can slip until a real backend exists to point at.
- **7** depends on 4 for the Sync UI half, and on nothing but the current codebase for the Reports UI half (that half could be pulled forward if reporting matters more than sync for the next release). The Release step at the end of 7 depends on everything else being merged. Plan 7 now also folds in WL-50 (Reports CSV export) and WL-51 (pre-merge safety backup) — see its addendum.
- **Recommended before 7: WL-67** (consolidate `MainWindowViewModel`'s 5-arg/6-arg constructors — one subtask of WL-52's constructor/`TaskItemViewModel` cleanup). Plan 7 threads a `SyncOrchestrator` dependency through that same constructor; landing WL-67 first avoids doing that edit twice. Not a hard blocker, just wasted motion if skipped.

## How to use these

Each plan file is written so it can be pasted as the *first message* of a fresh Claude Code session (or used
as the seed for `/plan` mode) with no other context required beyond the repo itself. Every plan includes:

- **Context** — why this milestone exists and what state the repo is in today (with real file/line references).
- **Scope** — the concrete steps to implement, in order.
- **Out of scope** — explicit non-goals, so the session doesn't scope-creep into the next milestone.
- **Acceptance criteria** — what "done" looks like, including tests.
- **Suggested commit message** — Conventional Commits style, matching this repo's convention.
- **Prompt for Claude Code** — a ready-to-paste one-paragraph kickoff prompt.

Work each plan on its own branch off `main` (e.g. `wl-47-housekeeping`), and merge before starting the next
one in the dependency chain.
