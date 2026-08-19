# Plan 6 — API Sync Provider ([WL-96](https://mnisan.atlassian.net/browse/WL-96))

> **Rewritten 2026-08-11.** This plan used to describe a *stub* against a hypothetical FastAPI backend that
> "does not exist yet." That is no longer true: the backend is a real repo (`WorkLedger-API`), it is
> **C#/ASP.NET Core + EF Core + Postgres**, not FastAPI, and its scaffold, data model, and auth (server plans
> S0/S1/S2) are merged. The section below replaces the old "document a contract nobody implements" scope with
> the **actual** wire contract, so the two repos cannot drift.
>
> **Server status update (2026-08-11, later the same day):** server plan S3 / **WL-76** and its subtasks
> WL-82/83/84 are **Done** and merged (`WorkLedger-API@1654a65`). `POST /sync/push`, `GET /sync/pull`,
> `POST /auth/register-device`, and `GET /auth/whoami` are all live. Earlier text describing S3 as "in flight"
> is stale.

## Context

`WorkLedger-API` is the authoritative datastore. It publishes a shared **`WorkLedger.Contracts`** library —
the DTOs on the wire are C# types compiled into both sides, which is the entire reason the server was built
in C# rather than Python. **This provider must consume those types rather than hand-rolling its own**, or the
compiler-enforced contract buys us nothing.

What already exists on the server (`WorkLedger-API`, `main`):

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /auth/register-device` | anonymous + `X-Bootstrap-Secret` header | `RegisterDeviceRequest(DeviceId, Label)` → `RegisterDeviceResponse(Token, ExpiresAtUtc)`. Idempotent per `DeviceId`. |
| `GET /auth/whoami` | bearer | `WhoAmIResponse(AccountId, DeviceId)` — useful as a cheap connectivity/token check. |
| `POST /sync/push` | bearer | `SyncPushRequest(Changes)` → `SyncPushResponse(Results, ServerTime)` (S3/WL-76, **shipped**). |
| `GET /sync/pull?since=<ISO8601>` | bearer | → `SyncPullResponse(Changes, ServerTime)` (S3/WL-76, **shipped**). |
| `GET /health` | anonymous | liveness. |

Every endpoint except `/health`, `/`, and `/auth/register-device` requires a valid JWT; the server is
single-account (v1) and every token resolves to `(AccountId, DeviceId)`, so all data access is
account-scoped server-side. There is nothing for this provider to do about tenancy.

Depends on Plan 4 (`ISyncProvider`, `SyncResult`, `RemoteChange`) — same as Plan 5, a second concrete provider
against the same abstraction, implemented independently of `FileSyncProvider`.

**Was blocked on two client-side bugs** (found while reconciling this contract; see "Prerequisites" below):
**WL-91** (entities carry no parent `SyncId`) and **WL-93** (deletes are queued without a version bump).
Without WL-91 every task and time entry this provider pushes is rejected by the server as an orphan. Both are
**fixed**, as the first two commits on this plan's branch.

Existing pattern to reuse for user-facing error surfacing: `Services/ToastService.cs` already spawns transient
`ToastWindow` notifications; connection failures should stay structured/catchable so Plan 7 can toast them.

## Server contract (normative — keep in sync with `WorkLedger-API/plans/S3-sync-endpoints.md`)

These are the rules the server implements. Where this provider has a choice, it has none — these decide it.
If any of them changes, it changes in **both** repos in the same breath.

- **Last-write-wins is a bare `incoming.Version > existing.Version`.** No timestamp tie-break, on either side.
  This mirrors `SyncOrchestrator.RemoteWins` (`Services/SyncOrchestrator.cs:177`) exactly and deliberately:
  `RemoteChange` has no `CreatedAt` field, so a timestamp rule is not implementable here, and a server that
  used one would silently disagree with us. **Do not add a timestamp tie-break to the orchestrator to "match"
  the server.**
- **`Version`/`OriginDeviceId` ride at the top level of the wire change.** `SyncQueue` does not carry them —
  they live inside `PayloadJson` (see `Models/SyncQueue.cs`). Lift them out when building a `SyncChangeDto`,
  exactly as `FileSyncProvider.ToWireEntry`/`ExtractVersionMetadata` already does
  (`Sync/FileSyncProvider.cs:130-163`). Reuse that logic rather than writing a second copy.
- **Deletes tombstone server-side and come back with a bumped version.** The server accepts a `"delete"` at
  `incoming.Version >= existing.Version` and stores it at `existing.Version + 1`, so the tombstone it hands to
  other devices strictly beats their copy. That accommodation exists only because of **WL-93**; it means an
  accepted delete's `AuthoritativeVersion` is *higher* than what we pushed — do not treat that as a rejection.
- **Push results are positional.** `SyncPushResponse.Results` has exactly one `SyncItemResult` per input
  change, in request order. `SyncItemResult.EntityId` is **not** a usable key — one batch can hold several
  queue rows for the same entity (create → update → delete). See **WL-94**.
- **`Accepted = false` is final — retire the queue row, don't retry it.** The server rejects for two reasons:
  a validation failure (bad payload, `Guid.Empty` parent, unknown entity type) or *its* version won. Neither
  changes on a re-push, and today's `SyncOrchestrator` leaves anything missing from `AcceptedSyncQueueIds`
  pending, so a naive mapping re-pushes those rows on every sync **forever**. Adopt
  `AuthoritativePayloadJson` when present *and* drop the queue row. See **WL-94**.
- **A same-version change from this device is accepted, not a conflict.** When the server's row was authored
  by this same `OriginDeviceId` at an equal or higher version, the change is already reflected — a replay, a
  self-echo, or an ack we never received — and the server answers `Accepted = true` without writing. So a
  `SyncResult` can legitimately accept a change the server did not apply; do not treat "accepted" as "the
  server's row now equals what I sent."
- **The pull cursor is server time.** `SyncPullResponse.ServerTime` is what the next `since` must be, not our
  own `UtcNow`. **WL-92 landed with this plan**: `ISyncProvider.PullAsync` now returns a `PullResult`
  (`Changes`, `ServerTime`, `Succeeded`), the orchestrator adopts `ServerTime` as its cursor and persists it
  through `ISyncCursorStore`, and a *failed* pull leaves the cursor alone — advancing past changes that were
  never delivered would skip them permanently, so a failure has to be distinguishable from an empty result.
- **JSON policy: PascalCase out, case-insensitive in.** Use the shared `SyncJson.Options` from
  `WorkLedger.Contracts` for **all** payload (de)serialization. This is not cosmetic: the orchestrator reads
  payloads with plain `JsonSerializer.Deserialize<T>(payloadJson)` and default, **case-sensitive** options
  (`Services/SyncOrchestrator.cs:181`), so a camelCase payload deserializes into an all-defaults entity with
  no exception thrown. A `JsonSerializerDefaults.Web` client here would corrupt data silently.
- **Timestamps are UTC.** The server normalizes everything it receives to `DateTimeKind.Utc` (its columns are
  `timestamptz`), and SQLite does not preserve `Kind` on our side, so send ISO-8601 and treat everything
  coming back as UTC. Format `since` as `{since:o}`.
- **Entity type and operation strings are fixed**: `"project"` / `"task"` / `"timeentry"` and
  `"create"` / `"update"` / `"delete"`. The server's `SyncEntityTypes` / `SyncOperations` constants in
  Contracts are defined *from* `LocalDbService.EntityTypes` / `SyncOperations`
  (`Services/LocalDbService.cs:16-27`). Keep using the local constants at call sites; assert equality with the
  Contracts constants in a test so a rename on either side fails the build rather than the sync.

## Prerequisites (do these first — this plan cannot be correct without them)

1. **WL-91 — parent `SyncId`s.** `TaskItemPayload` requires `ProjectSyncId` and `TimeEntryPayload` requires
   `TaskSyncId`, but `TaskItem` carries only `ProjectId` (local int) and `TimeEntry` only `TaskItemId`. Pushing
   without WL-91 sends `Guid.Empty` parents, which the server rejects per-item by design; pulling without it
   leaves the local FK int at `0`. This is the same bug class as WL-47/WL-53, one level down.
2. **WL-93 — deletes must bump the version.** Independently a live bug in file-based sync (a delete on one
   device never applies on another). The server compensates for the API path only.

WL-92 (persisted, server-authoritative cursor) and WL-94 (positional result mapping) are scoped *inside* this
plan's work rather than ahead of it.

**As built (branch `06-api-sync-provider`):** WL-93 and WL-91 landed as the first two commits on this branch,
ahead of the provider, rather than as separate PRs. WL-92 and WL-94 landed with it as planned.

Two further items were found while building this and deliberately left out of scope:

- **WL-97** — `TimerService` writes time entries through `LocalDbService`'s raw generic `InsertAsync`/
  `UpdateAsync` helpers rather than `SaveTimeEntryAsync`, so timer-produced entries get no `SyncQueue` row at
  all, no version stamping, and no parent-SyncId resolution. That is the app's primary time-entry path, so
  end-to-end API sync can't be demonstrated with timed work until it lands. Plan 03's boundary, not this one's.
- **WL-98** — the tombstone ambiguity in `SyncItemResult` described under Scope 4. Since shipped
  server-side and vendored (WL-100); consuming it client-side is **WL-101** (see the update under Scope 4).

## Scope

1. **Take a dependency on `WorkLedger.Contracts`.** Preferred: the published package (server plan S0 covers
   the GitHub Packages publish). If it isn't published when this is picked up, add the project reference or
   vendor the DTO files verbatim with a header comment naming the source commit — **do not** retype them into
   new shapes. Types used here: `SyncChangeDto`, `SyncPushRequest`, `SyncPushResponse`, `SyncItemResult`,
   `SyncPullResponse`, `ProjectPayload`, `TaskItemPayload`, `TimeEntryPayload`, `RegisterDeviceRequest`,
   `RegisterDeviceResponse`, plus `SyncJson.Options` and the entity-type/operation constants.

   **As built:** vendored. `WorkLedger.Contracts.csproj` has no packaging properties and the API repo's only
   workflow is `build.yml`, so there is nothing to reference; a cross-repo `ProjectReference` would build
   locally and fail CI, which checks out only this repo. The files live under `Sync/Contracts/` with upstream
   namespaces intact (so swapping in a real `PackageReference` later is a delete plus one line) and a header
   naming the source commit — originally `3e6e955`, re-vendored to `2065662` under WL-100. See
   `Sync/Contracts/README.md`.

2. **Settings** (`Services/SettingsService.cs`, following the `BackupDirectory`/`SyncFolderPath`
   empty-string-default + backfill pattern):
   - `ApiEndpoint` — server base URL.
   - `ApiToken` — the JWT **issued by the server**, not a hand-entered secret. Persisted so a restart doesn't
     re-bootstrap.
   - `ApiTokenExpiresAtUtc` — from `RegisterDeviceResponse.ExpiresAtUtc`, so the provider can re-bootstrap
     before it expires instead of discovering expiry as a 401.
   - `AppSettings.DeviceId` already exists (`Services/SettingsService.cs:19`) — a persisted per-install GUID
     string, minted at first run and backfilled for older settings files. That is exactly the `DeviceId` the
     server registers and stamps as `OriginDeviceId`. **Do not mint a second identifier.**
   - No Settings-tab UI here — that's Plan 7.

3. **Device bootstrap** (`Sync/ApiDeviceRegistration.cs` or a method on the provider): `POST
   /auth/register-device` with the `X-Bootstrap-Secret` header and `RegisterDeviceRequest(DeviceId, Label)`;
   store `Token` + `ExpiresAtUtc`. The bootstrap secret is operator-supplied, entered once (Plan 7 UI), and
   **not** persisted alongside the token if avoidable. Re-registration is idempotent server-side, so "lost my
   token" is a supported, non-destructive path: on a 401 from a sync call, re-bootstrap once and retry the
   call, then give up and surface a failure. Replaces the old plan's "single static bearer token."

4. **`Sync/ApiSyncProvider.cs`** implementing `ISyncProvider`, constructor-injected `HttpClient` (never
   `new HttpClient()` internally), base URL, and a token accessor:
   - `PushAsync(pendingChanges)`: map each `SyncQueue` row → `SyncChangeDto` (lifting `Version`/
     `OriginDeviceId` out of `PayloadJson`; `EntityId` = the queue row's `EntityId`, which is already the
     entity `SyncId`; `CreatedAt` = the queue row's `CreatedAt`), `POST` to `{baseUrl}/sync/push`, then map
     `Results` **positionally** back to `SyncQueue.Id` values → `SyncResult.AcceptedSyncQueueIds` /
     `Failures` (WL-94). Preserve the queue's own order in the request; the server re-orders internally for
     application but answers in request order.
   - When an item comes back `Accepted = false` with an `AuthoritativePayloadJson`, that payload is the
     winning record. Either apply it immediately or leave a clearly-marked seam for the orchestrator to do so
     — decide and document; do not silently drop it.

     **Decided: neither — request a re-pull instead, because adopting it directly is unsafe.**
     `SyncItemResult` has no field saying whether the winning row is a *tombstone*, and the payload DTOs carry
     no `IsDeleted` either. Reachable case: a third device deleted the entity (tombstone at v5) while this one
     pushes an update at v4 — `SyncService.ApplyUpsert`'s conflict branch hands back the tombstone's payload,
     and synthesizing an `"update"` from it would resurrect a deleted row locally and permanently. So the
     provider sets `SyncResult.RequiresFullPull` (an authoritative payload is present only when the server's
     row won — a validation failure passes `null` there) and `SyncOrchestrator` rewinds its cursor to
     `DateTime.MinValue` for the next pull, which states the operation explicitly. Note a plain incremental
     pull is not enough on its own: a losing item performs no server-side write by design, so the winner's
     `UpdatedAt` is already behind the cursor. Filed as **WL-98** to add an `AuthoritativeOperation` to the
     Contracts DTO and drop the rewind.

     > **Update (2026-08-19):** WL-98's server half has shipped — `SyncItemResult.AuthoritativeOperation`
     > (`"update"` / `"delete"`, null when there is no payload) is live and now vendored here. The client
     > half is still open as **WL-101**: `MapPushResults` does not read it, so the rewind is still what happens. Adopting
     > the payload directly using that field is a self-contained follow-up, and
     > `PushAsync_ServerWinsCarryingAnAuthoritativeOperation_StillRetiresAndRePulls` pins today's behaviour
     > for both `"update"` and `"delete"` so the change has a starting point to move off.
   - `PullAsync(since)`: `GET {baseUrl}/sync/pull?since={since:o}`, map each `SyncChangeDto` → `RemoteChange`,
     and surface `ServerTime` for the cursor (WL-92).
   - **Failure handling remains the core requirement**: unreachable host, DNS failure, timeout, non-2xx,
     malformed JSON, and expired token must all become a typed, catchable failure (a `SyncProviderException`
     or a failure-flagged `SyncResult`) — never an unhandled `HttpRequestException`/`TaskCanceledException`.
     `SyncMode = Api` pointed at a dead server must not crash the app.

5. **Tests**, using a mocked `HttpMessageHandler` (no real network):
   - Push sends the expected method/path/auth header/body shape and parses a success response into
     `SyncResult`.
   - **Positional mapping**: a batch containing two changes for the *same* entity maps each result to the
     correct `SyncQueue.Id` (the regression WL-94 exists to prevent).
   - An accepted delete whose `AuthoritativeVersion` is *higher* than what was pushed is still treated as
     accepted (the WL-93 server accommodation).
   - Pull parses into `RemoteChange`s and exposes `ServerTime`.
   - Bootstrap: a successful register stores token + expiry; a 401 from a sync call triggers exactly one
     re-bootstrap-and-retry, then a clean failure.
   - Every failure mode (unreachable, non-2xx, malformed JSON) surfaces as a typed failure, not an exception
     escaping the provider.
   - **Contract guards** (cheap, and they're what stop cross-repo drift):
     - The local `EntityTypes`/`SyncOperations` constants equal the Contracts constants.
     - A payload serialized by `LocalDbService`'s queue-building path deserializes into the matching Contracts
       payload DTO with **every** field populated — this is the test that would have caught WL-91.
     - A payload serialized through `SyncJson.Options` round-trips back through the orchestrator's plain
       `JsonSerializer.Deserialize<T>` — i.e. it is PascalCase.

## Out of scope

- **No server-side changes.** The endpoints, the LWW rule, the tombstone bump, and the positional-result
  contract are `WorkLedger-API`'s S3/WL-76. If something here needs the server to change, raise it there —
  do not work around it locally, that's how the two implementations drift.
- No Settings-tab UI for `ApiEndpoint`/`ApiToken`/bootstrap secret — Plan 7.
- No multi-user auth, login, or refresh-token rotation — the server is deliberately single-account for v1 with
  long-lived per-device tokens; re-bootstrap is the renewal path.
- No retry/backoff policy beyond "fail gracefully once" (plus the single re-bootstrap retry on 401) — the
  queue persists, so the next sync tick retries naturally.
- No conflict *merge* beyond last-write-wins.

## Acceptance criteria

- `Sync/ApiSyncProvider.cs` implements `ISyncProvider` against the real endpoints, using an injected
  `HttpClient` and the shared `WorkLedger.Contracts` DTOs — no locally-redefined wire types.
- A device with no token can bootstrap via `POST /auth/register-device` and persist the issued JWT + expiry;
  a 401 mid-sync triggers exactly one re-bootstrap-and-retry.
- Push results map back to the correct `SyncQueue.Id`s positionally, including when one batch holds multiple
  changes for one entity.
- Pull produces `RemoteChange`s the existing orchestrator merges unmodified, and `ServerTime` is available as
  the next cursor.
- Payload JSON is PascalCase in both directions and round-trips through the orchestrator's default-options
  deserializer.
- Every failure mode (unreachable host, non-2xx, malformed response, expired token) is caught inside the
  provider and surfaced as a structured failure — one test each.
- The contract-guard tests pass, so a rename or shape change in either repo fails the build rather than
  silently corrupting sync.
- All tests run against a mocked `HttpClient`; no real network calls.

## Suggested commit message

```
feat(sync): add ApiSyncProvider against the WorkLedger-API sync contract
```

## Prompt for Claude Code

> Implement `Sync/ApiSyncProvider.cs` against the **real** `WorkLedger-API` server (C#/ASP.NET Core — the old
> FastAPI description in `README.md` is obsolete). Read the "Server contract (normative)" section of
> `plans/06-api-sync-provider.md` first and follow it exactly; it mirrors `WorkLedger-API/plans/S3-sync-endpoints.md`
> and the two must not drift. Consume the shared `WorkLedger.Contracts` DTOs rather than defining wire types
> locally. Add `ApiEndpoint`, `ApiToken`, and `ApiTokenExpiresAtUtc` to `AppSettings` following the
> `SyncFolderPath` pattern, and reuse the existing `AppSettings.DeviceId` per-install GUID — do not mint a
> second one. Implement device bootstrap against `POST /auth/register-device` (`X-Bootstrap-Secret` header,
> returns a JWT + expiry, idempotent per device), and re-bootstrap once on a 401 before failing. `PushAsync`
> maps `SyncQueue` rows to `SyncChangeDto` (lifting `Version`/`OriginDeviceId` out of `PayloadJson` the way
> `FileSyncProvider.ToWireEntry` already does) and maps `SyncPushResponse.Results` back to queue ids
> **positionally, not by `EntityId`**; an accepted delete may legitimately come back with a *higher*
> `AuthoritativeVersion` than was pushed. `PullAsync` maps `SyncChangeDto` to `RemoteChange` and surfaces
> `ServerTime` as the next cursor. Use the shared `SyncJson.Options` (PascalCase out, case-insensitive in) for
> all payload JSON — a camelCase payload would silently deserialize to an empty entity in `SyncOrchestrator`.
> Keep the graceful-failure requirement: unreachable host, non-2xx, malformed JSON, and expired token all
> become typed failures, never unhandled exceptions. Test everything against a mocked `HttpMessageHandler`,
> including the positional-mapping regression and the contract-guard tests. Do **not** change the
> orchestrator's last-write-wins comparison, and do not change the server.
