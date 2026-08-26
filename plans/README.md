# WorkLedger Build Plans — Index

Plans **1–7** are derived from [`WorkLedger_MVP_Plan.md`](../WorkLedger_MVP_Plan.md) (the M0–M7 overview) and
have all merged, cutting **v0.1.0**. Plans **8–10** are the client half of the server's user-accounts release
(`WorkLedger-API` plans S6–S9) and are **not started** — see "The user-accounts release" below.

Each file in this folder is a **self-contained** plan meant to be handed to a Claude Code session as its own
unit of work — its own branch, its own PR, its own review. M7 (Release) is small enough that it's folded into
the M6 plan rather than getting its own file, so plans 1–7 cover the 8 original milestones.

> **Ticket numbers note:** plans 1–7 originally referenced WL-47 through WL-54 as their tickets, but those
> tickets were never actually created in Jira — the numbers were placeholders. That range has since been
> claimed by unrelated tickets (WL-47–WL-72: gap tickets covering global sync identifiers, retry/backoff,
> sync artifact retention, reports export, pre-merge safety backups, and MainWindowViewModel/TaskItemViewModel
> cleanup, plus subtasks added to WL-9). Where a plan below still says TBD, create its ticket fresh (it'll get
> whatever the next available WL-## number is) and update this table with the real number.

| # | File | Ticket(s) | Depends on | Status |
|---|------|-----------|------------|--------|
| 1 | [01-housekeeping.md](01-housekeeping.md) | TBD | — | merged |
| 2 | [02-data-model-versioning.md](02-data-model-versioning.md) | WL-73 | 1 | merged |
| 3 | [03-sync-queue-wiring.md](03-sync-queue-wiring.md) | TBD | 2 | merged |
| 4 | [04-sync-abstraction-orchestrator.md](04-sync-abstraction-orchestrator.md) | TBD | 2 (parallel with 3) | merged |
| 5 | [05-file-sync-provider.md](05-file-sync-provider.md) | TBD | 4 | merged |
| 6 | [06-api-sync-provider.md](06-api-sync-provider.md) | [WL-96](https://mnisan.atlassian.net/browse/WL-96) | 4, ~~WL-91~~, ~~WL-93~~ (both fixed on plan 6's branch) | merged |
| 7 | [07-sync-reports-ui-and-release.md](07-sync-reports-ui-and-release.md) | TBD (folds in WL-50, WL-51, WL-67, WL-102) | 4, 5 | merged (v0.1.0) |
| 8 | [08-user-accounts-login-cutover.md](08-user-accounts-login-cutover.md) | [WL-133](https://mnisan.atlassian.net/browse/WL-133), [WL-134](https://mnisan.atlassian.net/browse/WL-134), [WL-135](https://mnisan.atlassian.net/browse/WL-135), [WL-137](https://mnisan.atlassian.net/browse/WL-137), [WL-138](https://mnisan.atlassian.net/browse/WL-138), [WL-145](https://mnisan.atlassian.net/browse/WL-145) | 6, 7, **API S9** | not started — blocked on the server |
| 9 | [09-auth-capabilities-and-account-ui.md](09-auth-capabilities-and-account-ui.md) | [WL-146](https://mnisan.atlassian.net/browse/WL-146) | 8 | not started |
| 10 | [10-account-management.md](10-account-management.md) | [WL-139](https://mnisan.atlassian.net/browse/WL-139) (WL-140…WL-144) | 8, 9, **API S8** | not started |

## Sequencing

### Plans 1–7 (merged)

- **1 and 2 are prerequisites for everything else** — run them first, in order.
- **3 and 4 can run in parallel** once 2 lands (different files: 3 touches `LocalDbService`, 4 adds a new `Sync/` abstraction + `SyncOrchestrator`).
- **5 and 6 both depend on 4** but not on each other. 5 (file-based) has merged. **6 no longer waits on a backend** — see "Cross-repo: the server" below — but it *is* blocked on WL-91 and WL-93.
- **7 has merged**, and with it every plan of the original MVP set. It folded in WL-50 (Reports CSV export), WL-51 (pre-merge safety backup), WL-67 (constructor consolidation — done first, as recommended) and WL-102 (the ISO week fix). Its release step is what cut **v0.1.0**.

### Plans 8–10 (the user-accounts cutover)

- **8 → 9 → 10 are strictly linear.** They touch the same files — `Sync/ApiSyncProvider.cs`,
  `Services/SyncCoordinator.cs`, `ViewModels/MainWindowViewModel.Sync.cs`, `Views/MainWindow.axaml` — so there
  is nothing to parallelise, and 9 and 10 both reference DTOs that only exist once 8's re-vendor lands.
- **8 cannot start until the server's S9 has merged.** It re-vendors `WorkLedger.Contracts` and calls
  `POST /auth/login`; neither exists yet. As of 2026-08-26 the API repo's `main` is at `ced79aa` and S6–S9 are
  plan files only.
- **8 is one PR containing several tickets on purpose.** Re-vendoring (WL-133) deletes
  `RegisterDeviceRequest`/`Response`, which `ApiSyncProvider` and its tests reference — so WL-133 alone leaves
  `main` not compiling. It is a *commit*, not a PR; WL-134/135/137 are what put the tree back together, and 8
  bundles them plus WL-138's data guard and the minimum sign-in UI (WL-145) that keeps the app usable end to
  end.
- **WL-136 spans plans 8 and 9**, so it carries a subtask for each: WL-145 (the plain sign-in row, plan 8) and
  WL-146 (the capability-driven registration/claim/revocation-routing, plan 9). Close the subtasks, not the
  parent, until both have landed.

## The user-accounts release

The server's S6–S9 make **a user account the tenancy root**: `Account` becomes `User` with email/password
credentials, one user owns many devices, and `POST /auth/register-device` is **deleted** in favour of
`POST /auth/login`. It is a breaking change on the wire, released together, with `POST /auth/claim` as the
upgrade path for a deployment that already holds data.

Two Jira epics split the work by repo: **[WL-104](https://mnisan.atlassian.net/browse/WL-104)** is the server
(WL-106 → WL-107 → WL-108 → WL-109, one Task per plan S6–S9), and
**[WL-105](https://mnisan.atlassian.net/browse/WL-105)** is this repo (WL-133 → WL-134/135/136/137/138, then
WL-139, with the re-vendor blocking the rest). Keep using the `server` / `client` labels.

What actually changes here, in one paragraph: the client goes from "sends a static token obtained with the
bootstrap secret" to "has a sign-in form". The **sync wire format does not change at all** — `SyncChangeDto`,
the PascalCase payload policy, the positional push results and the last-write-wins rule all come through
untouched, and if any of them moved it was an accident on the server side. What *does* change is authentication
and, with it, the meaning of a 401: the server can now revoke a token (a bumped security stamp, a deleted
device row, a deleted account), so a 401 mid-sync means "you have been signed out" rather than "your token aged
out", and the right answer is to ask for a sign-in rather than to retry. Retrying is actively harmful — S7 adds
per-user lockout at `Auth:MaxFailedLogins` (5) consecutive failures, and a locked account refuses the *correct*
password too, so a background sync that answers every 401 with a login attempt locks the user out of their own
server unattended, in about an hour at the default 15-minute interval.

Plan 8 item 4 resolves that, and the resolution turns on one observation: **a 401 from `/auth/login` is
deterministic**, so the right automated retry count for a rejected credential is zero, not "a few with
backoff". That gives **Invariant L** — at most one unattended failed login attempt per credential, ever, after
which the credential is discarded — which cannot reach a five-failure threshold no matter how long the app
runs, and which still permits a remember-me feature later because it constrains the mechanism rather than the
feature. Two supporting properties do most of the work: `423` is only ever returned by `/auth/login`, and no
sync call touches that endpoint, so the background timer *cannot* contribute to a lockout at all; and the timer
skips its run outright while the session isn't signed in, rather than firing a doomed request every tick.

Designing it also turned up two defects in S7's own lockout rules and one missing capability field, all filed
server-side before that code exists — see plan 8 item 4½.

One hazard has no server-side counterpart and nothing else will catch it: the local SQLite database has **no
user column**, so signing in as a different user against the same database pushes one user's rows into
another's account, irreversibly. Unreachable while the server had a single seeded account — the database had
exactly one possible owner, so it needed no way to record one — and one typo in a login form afterwards.

Plan 8 item 6 handles it, and **offline-first decides the shape of the fix**. The marker goes in a one-row
`DatabaseOwner` table *inside* `workledger.db` rather than in `AppSettings`, because backup/restore, the
data-directory move and the pre-merge backups all separate settings from the database, and a settings-based
guard passes silently the moment they are. An unstamped database is adoptable by any account with **no prompt
at all** — working offline for a week and then connecting is the product's core promise, not an edge case — and
a mismatch is refused outright rather than confirmed, since no modal wording makes commingling two people's
timesheets a reasonable thing to consent to. Sign-out is an auth event, never a data event.

Two alternatives are ruled out in writing there so they don't resurface: **archive-and-wipe** on account change
(what most apps do, and it inverts offline-first — empty app until a successful pull, nothing at all if the
server is unreachable), and a **`UserId` column on every entity** (buys a cross-account local view nobody asked
for, pays with a migration across the orchestrator's merge lookups). Per-account profiles —
[WL-148](https://mnisan.atlassian.net/browse/WL-148) — are the wanted-case follow-up and deliberately not in
this release.

## Cross-repo: the server

The backend is a **real, separate repo** — `WorkLedger-API` (`E:\GitHub-Repos\WorkLedger-API`) — with its own
plan set. It is **C# / ASP.NET Core + EF Core + Postgres**, *not* the FastAPI/SQLAlchemy service this repo's
`README.md` used to describe (corrected by Plan 7 item 10). **S0–S5 have merged** (the deployable
single-account server, v0.1.0); **S6–S9 are written but not implemented**.

The two repos meet **only** at `WorkLedger.Contracts`, the shared DTO library. Plan 6 consumes it rather than
defining its own wire types — that shared-compiler-contract is the reason the server is C#. There is no
published package, so `Sync/Contracts/` is a **vendored, byte-identical copy** pinned at a named upstream
commit; read `Sync/Contracts/README.md` before touching it, and never edit a file under it to fix a
client-side problem. Nothing detects automatically that the copy has fallen behind — check with
`git log --oneline <pinned-commit>..HEAD -- src/WorkLedger.Contracts` in the API repo.
`Tests/WorkLedger.Tests/SyncContractGuardTests.cs` is the drift guard that makes a rename fail the build
rather than the sync.

Reconciling this repo against the server's S3 plan surfaced four cross-repo gaps, all filed here:

| Ticket | Gap | Where it's fixed |
|---|---|---|
| WL-91 | `TaskItem`/`TimeEntry` carry no parent `SyncId` (only local FK ints) — payloads are unusable in both directions | Its own ticket; **blocked Plan 6** |
| WL-92 | Pull cursor is the client clock and resets to `MinValue` each launch | Its own ticket, folded into Plan 7 item 2 |
| WL-93 | Deletes are queued without a version bump, so no peer ever applies them (a live bug in file-based sync at the time) | Its own ticket; **blocked Plan 6** |
| WL-94 | Push acks key differently on each side — map results positionally | Scoped inside Plan 6 |
| WL-102 | Week boundaries disagreed: the client's weekly total started the week on **Sunday**, the server's `/reports/weekly` on **Monday** (ISO-8601). Same account, different seven-day range | **Fixed in Plan 7** — `ReportsService.StartOfIsoWeek` is now the one definition, used by both the header total and the Reports tab |

Later cross-repo work: **WL-98** (server states whether a winning payload is a tombstone), **WL-100**
(re-vendor), **WL-101** (consume `AuthoritativeOperation` instead of the full-pull cursor rewind) — all closed.
**WL-105** is the next one, and it is Plans 8–10.

**Rule of thumb:** the reconciliation rules live in `WorkLedger-API/plans/S3-sync-endpoints.md` and on
`SyncService`'s XML doc, and Plan 6 restates the client-facing half of them. If either copy changes, change both
in the same breath — and never "fix" a mismatch by quietly adjusting one side's behavior (e.g. adding a
timestamp tie-break to `SyncOrchestrator.RemoteWins` to match a server that has one). Silent divergence in the
merge rule is the one bug class this whole arrangement exists to prevent.

Server work reads this repo freely but does not commit to it, and the reverse holds too: a client plan reads
`WorkLedger-API` for reference and lands nothing in it. A change that needs both sides is two PRs in two repos,
in the same breath.

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

Plans 8–10 are the exception to "no other context required": each names the `WorkLedger-API` plan files it
derives from, and Plan 8 depends on `WorkLedger-API/plans/S9-client-handoff.md`, which the server's S9 produces.
**Read the handoff and treat it as authoritative** where it disagrees with a plan here — these were written
from the server's plan documents, before the server built anything.
