# Plan 6 — API Sync Provider (Stub) (ticket TBD — see note in README.md)

## Context

The FastAPI + Postgres backend described in `README.md` (`README.md:143-150`, `:176-194`) does not exist yet
and is out of scope for this client-side workstream. Per `WorkLedger_MVP_Plan.md`'s sequencing notes, this
plan **can slip until the backend team has something to actually point at** — Plan 5 (file-based sync) is the
one that matters for near-term user value. Build this now only to the extent that it keeps the client ready
to flip on real sync the moment a backend exists, without a second implementation phase later.

`README.md`'s sync section describes the contract this provider should target:
- Client bundles queued changes and sends them to the API (`README.md:170-172`).
- Server compares server version vs. client version vs. timestamps, resolves conflicts, and returns updated
  authoritative records + new version numbers + changes from other devices (`README.md:178-187`).

Since there's no real endpoint yet, this plan's entire purpose is: implement against a documented contract,
mock `HttpClient` for tests, and fail *gracefully* (no crash, clear user-facing error) when pointed at a URL
that isn't actually serving this API.

Depends on Plan 4 (`ISyncProvider`, `SyncResult`, `RemoteChange`) — same as Plan 5, this is a second concrete
provider against the same contract, implemented independently of `FileSyncProvider`.

Existing pattern to reuse for user-facing error surfacing: `Services/ToastService.cs` already spawns
transient `ToastWindow` notifications positioned relative to `desktop.MainWindow` — connection failures here
should go through that same mechanism (Plan 7 wires this into the UI/settings; this plan should just make
sure failures are structured/catchable so a toast *can* be shown, e.g. via a custom exception type or a
result object with an error message, not just an unhandled `HttpRequestException`).

## Scope

1. **Document the REST contract** this provider targets (a short block of comments at the top of the file is
   sufficient — this is a stub, not a shared API spec): `POST /sync/push` (batch of pending changes, bearer
   token auth), `GET /sync/pull?since=<ISO8601>` (returns authoritative updates + new version numbers +
   changes from other devices). Base URL and token are configurable — not hardcoded.
2. **`Sync/ApiSyncProvider.cs`** implementing `ISyncProvider`:
   - Constructor takes an `HttpClient` (injected, so it's mockable in tests — don't `new HttpClient()`
     internally), a base URL, and a bearer token.
   - `PushAsync(pendingChanges)`: serialize the batch as JSON, `POST` to `{baseUrl}/sync/push` with
     `Authorization: Bearer {token}`, parse the response into a `SyncResult` (accepted queue IDs / per-item
     failures).
   - `PullAsync(since)`: `GET {baseUrl}/sync/pull?since={since:o}` with the bearer token, parse the response
     into `RemoteChange`s.
   - **Failure handling is the core requirement here**: wrap both calls so that connection failures (no
     server, DNS failure, timeout, non-2xx response, malformed JSON) are caught and surfaced as a typed
     failure (e.g. throw a specific `SyncProviderException` with a clear message, or return a `SyncResult`
     with a failure flag/message rather than letting an unhandled `HttpRequestException`/`TaskCanceledException`
     propagate) — the app must not crash when `SyncMode = Api` is selected but no real backend is reachable.
     This is the single most important behavior in this plan, since it's the one path in the whole sync
     feature that's guaranteed to be exercised against something that doesn't exist yet.
3. **Config storage**: add `ApiEndpoint` and `ApiToken` fields to `AppSettings`
   (`Services/SettingsService.cs:9-19`), following the same empty-string-default/backfill pattern as
   `BackupDirectory`/`SyncFolderPath`. Don't build the Settings-tab UI for these yet — that's Plan 7, same
   division of labor as Plan 5's `SyncFolderPath`.
4. **Tests**, using a mocked `HttpMessageHandler` (standard approach for mocking `HttpClient` in .NET — wrap
   in a fake handler returning canned `HttpResponseMessage`s, inject via `new HttpClient(fakeHandler)`):
   - `PushAsync` sends the expected request shape (method, path, auth header, body) and parses a successful
     response into `SyncResult`.
   - `PullAsync` sends the expected request and parses a successful response into `RemoteChange`s.
   - A non-2xx response, a connection failure (simulate via a handler that throws), and a malformed JSON
     response are all caught and surfaced as a typed failure — **not** an unhandled exception escaping the
     provider.

## Out of scope

- No real backend to test against — everything here is validated against mocks. Don't attempt to stand up a
  FastAPI service or write server-side code; that's explicitly a separate, not-yet-started workstream per
  `WorkLedger_MVP_Plan.md` section 1.
- No Settings-tab UI for `ApiEndpoint`/`ApiToken` — Plan 7.
- No retry/backoff policy beyond "fail gracefully once" — same as Plan 4's orchestrator, this is future work,
  not required for the stub to be useful.
- No token refresh/auth flow — a single static bearer token stored in settings is sufficient for this stub;
  real auth (per `CLAUDE.md`, the `Auth/` folder is an empty placeholder) is explicitly deferred until the
  backend exists.

## Acceptance criteria

- `Sync/ApiSyncProvider.cs` implements `ISyncProvider` against the documented contract, using an injected
  `HttpClient`.
- `AppSettings.ApiEndpoint` / `ApiToken` exist and persist following the existing settings pattern.
- Every failure mode (unreachable host, non-2xx, malformed response) is caught inside the provider and
  surfaced as a structured failure rather than an unhandled exception — verified by a test for each case.
- All tests run against a mocked `HttpClient`, no real network calls.

## Suggested commit message

```
feat(sync): add ApiSyncProvider stub against documented REST contract
```

## Prompt for Claude Code

> Implement `Sync/ApiSyncProvider.cs` against the REST contract described in `README.md`'s sync section
> (`POST /sync/push` for batch push, `GET /sync/pull?since=...` for pull of authoritative updates with new
> version numbers, both bearer-token authenticated). Use an injected `HttpClient` with a configurable base
> URL and token stored via `AppSettings`/`SettingsService` (add `ApiEndpoint`/`ApiToken` fields following the
> existing `BackupDirectory` pattern). This is a stub against a backend that doesn't exist yet, so the most
> important requirement is graceful failure: connection failures, non-2xx responses, and malformed responses
> must all be caught and surfaced as a structured error, never an unhandled exception that could crash the
> app. Mock the `HttpClient` (fake `HttpMessageHandler`) in tests and cover both the success paths and every
> failure mode.
