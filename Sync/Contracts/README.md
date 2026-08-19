# Vendored `WorkLedger.Contracts`

These files are **copies**, not originals. The source of truth is
[`WorkLedger-API`](https://github.com/dante286/WorkLedger-API), at
`src/WorkLedger.Contracts/`, as of commit **`2065662`** (the last commit to touch that project;
repo `main` was at `ced79aa`).

Re-vendored 2026-08-19 from the previous pin (`3e6e955`) — WL-100. The only shape change since then was
`SyncItemResult.AuthoritativeOperation` (WL-98) — see "What the server tells us on a rejection" below.
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

`ApiSyncProvider` does not read the field yet: it still answers a server-wins rejection by setting
`SyncResult.RequiresFullPull`, which rewinds the cursor so the next pull re-delivers the row and
states its operation explicitly. That is correct but expensive — it re-pulls the account's history on
every server-wins conflict. Consuming `AuthoritativeOperation` directly is the cheaper path and is
tracked as **WL-101**; the field was vendored ahead of needing it for that reason.

## Replacing this folder with the real package

When the API repo publishes `WorkLedger.Contracts`, delete this folder and add the
`PackageReference`. Namespaces here are upstream's verbatim (`WorkLedger.Contracts.*`), so nothing
else has to change.
