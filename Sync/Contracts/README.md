# Vendored `WorkLedger.Contracts`

These files are **copies**, not originals. The source of truth is
[`WorkLedger-API`](https://github.com/dante286/WorkLedger-API), at
`src/WorkLedger.Contracts/`, as of commit **`eb743d4`** (the last commit to touch that project;
repo `main` was at `414f451`).

Re-vendored 2026-09-30 from the previous pin (`3d27c86`) — WL-174, the client half of the server's
S10 and S11 work (see `WorkLedger-API/plans/S11-client-handoff.md`, which is authoritative for this
re-vendor). The client never re-vendored at S10's `1e343c8`, so this one pass picks up both. The
entire difference is **three trailing, defaulted parameters** in `AuthDtos.cs`, plus XML doc text:

| Record | Parameter | From | Default |
|---|---|---|---|
| `RegisterUserRequest` | 4th: `string? InviteToken` | S10 | `null` |
| `AuthCapabilitiesResponse` | 7th: `bool RegistrationAcceptsInvite` | S10 | `false` |
| `AuthCapabilitiesResponse` | 8th: `string? PortalUrl` | S11 | `null` |

No record was added, removed, renamed or reordered, so every existing call site compiles unchanged.
S10's admin records (`AdminDtos.cs`) and `ResetPasswordRequest` are **not** here: S11 moved them out of
`WorkLedger.Contracts` into the server, under the rule that the library holds only what the desktop
client speaks. The client never compiled against them, so their absence is not a removal.
**`Sync/` and `Payloads/` are byte-identical** to the previous pin — `git diff 3d27c86 eb743d4 --
src/WorkLedger.Contracts/Sync src/WorkLedger.Contracts/Payloads src/WorkLedger.Contracts/Reports` is
empty, as is the same diff from `1e343c8`. Only the provenance headers moved.

`PortalUrl` is the absolute address of the server's web portal, or `null` for a pre-S11 server or one
with the portal switched off. It is built from the operator's configured public address, never from
the request, so the client opens it as given and never derives one from `ApiEndpoint`.

Before that, WL-133 re-vendored `3d27c86` on 2026-09-15 from `2065662` — the client half of the
server's S6–S8 user-accounts work (see `WorkLedger-API/plans/S9-client-handoff.md`, which is
authoritative for that cutover). **Everything under `Sync/` and `Payloads/` came through untouched** — verified
file-by-file, the sole difference being one XML doc word in `SyncConstants.cs` (`(AccountId, SyncId)` →
`(UserId, SyncId)`). The push/pull shapes, `SyncJson.Options`' PascalCase policy, the positional
`SyncPushResponse.Results` and `SyncItemResult.AuthoritativeOperation` are all exactly what they were.
The change is confined to `AuthDtos.cs` — see "How a token is obtained, and how it dies" below.

`ReportDtos.cs` is deliberately **not** vendored: the client computes its own local reports and never
calls `/reports/*`.

## Why they're copied

The whole reason the server is written in C# rather than Python is that the DTOs on the wire are
the *same compiled types* on both sides, so the compiler catches contract drift. Ideally this
project would take a `PackageReference` on a published `WorkLedger.Contracts` package.

It can't yet: `WorkLedger.Contracts.csproj` has no packaging properties and the API repo publishes
nothing (its only workflow is `build.yml`). A cross-repo `ProjectReference` was the other option and
was rejected — this repo's CI checks out only this repo, so it would build locally and fail in CI.

## Rules

- **Do not edit these files to fix a client-side problem.** They are the server's shape. If
  something here is wrong, change it in `WorkLedger-API` and re-copy — otherwise the two
  implementations drift silently, which is the exact failure this design exists to prevent.
- Type declarations are byte-identical to upstream. The only additions are explicit `using`
  directives (this project doesn't enable `ImplicitUsings`) and a provenance header.
- `Tests/WorkLedger.Tests/SyncContractGuardTests.cs` asserts these still line up with the client's
  own constants and payload shapes, so a rename on either side fails the build rather than the sync.

## How a token is obtained, and how it dies

`AuthDtos.cs` is the only file WL-133's re-vendor actually changed, and it changed completely.
`RegisterDeviceRequest`/`RegisterDeviceResponse` are **gone**: `POST /auth/register-device` no longer
exists server-side, and `POST /auth/login` (`LoginRequest` → `LoginResponse`) is now the only thing
that mints a token. Enrolling the device is a side effect of logging in, on the same `DeviceId` this
install already stamps as `OriginDeviceId` — there is still exactly one device identifier.
`WhoAmIResponse` was reshaped from `(AccountId, DeviceId)` to `(UserId, Email, DisplayName, DeviceId)`.

The consequence that matters more than the renames: **a 401 is now a statement about the session, not
about the token's age.** The server validates a security stamp on every request, so a token can be
killed mid-session by a password change, a sign-out-everywhere, the device row being deleted from
another machine, or the account being deleted — none of which this install can see coming. "My token
aged out, get another one" was a safe reading under device bootstrap, where re-authenticating needed
only a secret the app held. It is the wrong reading now: re-authenticating needs a password, which this
client deliberately does not store, and the server locks an account after five consecutive failed
logins. The correct response to a 401 is therefore to ask a human to sign in, never to retry.

## Vendored but not called, and why that is not drift

The client's live auth contract is `LoginRequest`, `LoginResponse`, `UserSummary`, `WhoAmIResponse`
and `AuthCapabilitiesResponse` — `POST /auth/login`, `GET /auth/whoami` and `GET /auth/capabilities`.

Plan 12 (WL-173) moves every account operation to the server's web portal, so the client stops
calling the routes behind four more records: `RegisterUserRequest` (`/auth/register`, `/auth/claim`),
`ChangePasswordRequest` (`/auth/password`), `DeviceSummary` (`/auth/devices`) and
`DeleteAccountRequest` (`DELETE /auth/account`). **They are still vendored, deliberately.** Upstream
still carries them, because the server keeps those routes alive as documented compatibility routes for
v0.1.x clients in the field and operators' scripts. A record here that nothing in the client calls is
therefore **not** drift and not dead code to tidy away: this folder is a faithful copy, and they leave
when upstream removes them at its next release that breaks the wire on purpose.

## What the server tells us on a rejection

`SyncItemResult` carries three fields that only mean something together, and the combination is how a
rejection is classified:

| `Accepted` | `AuthoritativePayloadJson` | Meaning |
|---|---|---|
| `true` | null | Applied, or accepted as a replay/self-echo the server chose not to write. |
| `false` | **set** | The server's own row won. The payload is the winner, and `AuthoritativeOperation` says what it *is* — `"update"` for a live row, `"delete"` for a tombstone. |
| `false` | null | Validation failure. `Reason` says why. |

`AuthoritativeOperation` (WL-98) exists because the payload alone is not self-describing. Adopting a
tombstone's payload as an "update" resurrects a deleted entity locally and permanently, and no later
incremental pull corrects it — a losing push performs no server-side write, so the winner's
`UpdatedAt` is already behind the client's cursor.

`ApiSyncProvider` reads the field (WL-101). A rejection carrying both a payload and an operation is
turned into a `RemoteChange` on `SyncResult.AuthoritativeChanges`, which `SyncOrchestrator` merges
through the same path a pulled change takes — so a conflict costs one merge.

A payload arriving *without* an operation — a server predating WL-98, or an operation this client
doesn't recognize — falls back to `SyncResult.RequiresFullPull`, which rewinds the cursor so the next
pull re-delivers the row and states its operation explicitly. Correct, but it re-pulls the account's
history, which is why it is now only the fallback.

## Replacing this folder with the real package

When the API repo publishes `WorkLedger.Contracts`, delete this folder and add the
`PackageReference`. Namespaces here are upstream's verbatim (`WorkLedger.Contracts.*`), so nothing
else has to change.
