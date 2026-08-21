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
| 6 | [06-api-sync-provider.md](06-api-sync-provider.md) | [WL-96](https://mnisan.atlassian.net/browse/WL-96) | 4, ~~WL-91~~, ~~WL-93~~ (both fixed on plan 6's branch) |
| 7 | [07-sync-reports-ui-and-release.md](07-sync-reports-ui-and-release.md) | TBD (folds in WL-50, WL-51, WL-67, WL-102) | 4, 5 |

## Sequencing

- **1 and 2 are prerequisites for everything else** — run them first, in order.
- **3 and 4 can run in parallel** once 2 lands (different files: 3 touches `LocalDbService`, 4 adds a new `Sync/` abstraction + `SyncOrchestrator`).
- **5 and 6 both depend on 4** but not on each other. 5 (file-based) has merged. **6 no longer waits on a backend** — see "Cross-repo: the server" below — but it *is* blocked on WL-91 and WL-93.
- **7 has merged**, and with it every plan in this folder. It folded in WL-50 (Reports CSV export), WL-51 (pre-merge safety backup), WL-67 (constructor consolidation — done first, as recommended) and WL-102 (the ISO week fix). Its release step is what cut **v0.1.0**.

## Cross-repo: the server

The backend is a **real, separate repo** — `WorkLedger-API` — with its own plan set (`plans/S0`–`S5`). It is
**C# / ASP.NET Core + EF Core + Postgres**, *not* the FastAPI/SQLAlchemy service this repo's `README.md` used
to describe (corrected by Plan 7 item 10). All of its plans S0–S5 have merged.

The two repos meet **only** at `WorkLedger.Contracts`, the shared DTO library. Plan 6 consumes it rather than
defining its own wire types — that shared-compiler-contract is the reason the server is C#.

Reconciling this repo against the server's S3 plan surfaced four cross-repo gaps, all filed here:

| Ticket | Gap | Where it's fixed |
|---|---|---|
| WL-91 | `TaskItem`/`TimeEntry` carry no parent `SyncId` (only local FK ints) — payloads are unusable in both directions | Its own ticket; **blocks Plan 6** |
| WL-92 | Pull cursor is the client clock and resets to `MinValue` each launch | Its own ticket, or folded into Plan 7 item 2 |
| WL-93 | Deletes are queued without a version bump, so no peer ever applies them (a live bug in file-based sync today) | Its own ticket; **blocks Plan 6** |
| WL-94 | Push acks key differently on each side — map results positionally | Scoped inside Plan 6 |
| WL-102 | Week boundaries disagreed: the client's weekly total started the week on **Sunday**, the server's `/reports/weekly` on **Monday** (ISO-8601). Same account, different seven-day range | **Fixed in Plan 7** — `ReportsService.StartOfIsoWeek` is now the one definition, used by both the header total and the Reports tab |

**Rule of thumb:** the reconciliation rules live in `WorkLedger-API/plans/S3-sync-endpoints.md`, and Plan 6
restates the client-facing half of them. If either copy changes, change both in the same breath — and never
"fix" a mismatch by quietly adjusting one side's behavior (e.g. adding a timestamp tie-break to
`SyncOrchestrator.RemoteWins` to match a server that has one). Silent divergence in the merge rule is the one
bug class this whole arrangement exists to prevent.

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
