# Vendored `WorkLedger.Contracts`

These files are **copies**, not originals. The source of truth is
[`WorkLedger-API`](https://github.com/dante286/WorkLedger-API), at
`src/WorkLedger.Contracts/`, as of commit **`3e6e955`** (the last commit to touch that project;
repo `main` was at `1654a65`).

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

## Replacing this folder with the real package

When the API repo publishes `WorkLedger.Contracts`, delete this folder and add the
`PackageReference`. Namespaces here are upstream's verbatim (`WorkLedger.Contracts.*`), so nothing
else has to change.
