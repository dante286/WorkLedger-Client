# Plan 1 — Housekeeping (ticket TBD — see note in README.md)

## Context

`WorkLedger_MVP_Plan.md` identified two blockers that everything else in the MVP plan sits on top of:

1. As of the plan's writing, `theme-rework` had uncommitted work-in-progress that needed to land before new
   feature branches could be cut from a clean `main`. **Re-check this before starting** — `git status` and
   `git log main..theme-rework` — since the branch may already be clean or merged by the time this plan is
   picked up. If `git status` is clean and `theme-rework` is merged (or `main` already has the latest theming
   work), skip straight to step 2.
2. There is **no test project in the repo at all**. `.github/workflows/build.yml` already has a `dotnet test`
   step (`.github/workflows/build.yml:30-31`, `dotnet test --configuration Release --no-build`), but it's
   currently a silent no-op — there's nothing for it to discover and run. Every milestone after this one (data
   versioning, sync queue, orchestrator, providers) needs real test coverage, especially anything touching
   conflict resolution — so this has to exist before that work starts, not be bolted on after.

`WorkLedger-Client.slnx` today references only the single app project:

```xml
<Solution>
  <Project Path="WorkLedger-Client.csproj" />
</Solution>
```

There's no `Tests/` folder yet.

## Scope

1. **Resolve the branch state.**
   - Run `git status` and `git log --oneline main..HEAD` (or `main..theme-rework` if not already on it).
   - If there are uncommitted changes: review them, group into logically-scoped commits with Conventional
     Commit messages, and commit. If any change looks unintentional or half-finished, stop and ask rather than
     guessing.
   - If `theme-rework` has commits not yet on `main`, open a PR to merge it (don't force-merge — let the user
     review/approve, per this repo's stated review flow).
2. **Scaffold a test project.**
   - Add `Tests/WorkLedger.Tests/WorkLedger.Tests.csproj` targeting `net10.0`, using **xUnit** (there's no
     existing convention to match, and xUnit is the most common default for new .NET projects using
     `dotnet new xunit`).
   - Reference the main project (`WorkLedger-Client.csproj`) as a `ProjectReference` so models/services are
     directly testable. Note the app's `OutputType` is `WinExe` — this is fine for a `ProjectReference` from a
     test project but confirm `dotnet test` in the Tests project builds cleanly against it (Avalonia app
     assemblies referencing platform-specific bits can be finicky in CI on non-Windows runners — verify all
     three OS matrix legs in `build.yml` still pass).
   - Add the new project to `WorkLedger-Client.slnx`:
     ```xml
     <Solution>
       <Project Path="WorkLedger-Client.csproj" />
       <Project Path="Tests/WorkLedger.Tests/WorkLedger.Tests.csproj" />
     </Solution>
     ```
   - Write a handful of real starter tests — not placeholders — against existing, stable code. Good
     candidates that exist today and have clear expected behavior:
     - `SettingsService.LoadSettings()` migrating the legacy `BackupInterval.OnClose = 999` sentinel to
       `BackupOnClose = true` / `BackupInterval.Disabled` (`Services/SettingsService.cs:138-144`).
     - `SettingsService.ValidateBackupSource()` returning true/false based on presence of `settings.json` +
       `workledger.db` in a temp directory (`Services/SettingsService.cs:227-240`).
     - `LocalDbService.SaveProjectAsync` inserting when `Id == 0` vs. updating otherwise
       (`Services/LocalDbService.cs:62-68`), using a temp SQLite file.
3. **Wire CI.** `build.yml`'s existing `dotnet test --configuration Release --no-build` step should now
   actually discover and run the new project once it's added to the `.slnx` and restored/built in the prior
   steps — confirm no additional workflow changes are needed, and adjust if `dotnet build`/`dotnet test`
   need an explicit project/solution path.

## Out of scope

- Don't write tests for sync/versioning logic yet — that code doesn't exist until Plans 2–6.
- Don't refactor `MainWindowViewModel` or touch the three-constructor inconsistency noted in `CLAUDE.md` —
  unrelated to this milestone.
- Don't add a code coverage tool, linter, or formatter unless asked — CLAUDE.md notes none is currently
  configured; adding one is a separate decision.

## Acceptance criteria

- `git status` is clean; any prior WIP is either committed with clear messages or explicitly called out to
  the user as needing a decision.
- `dotnet test` run locally from repo root discovers and passes the new test project.
- CI's existing `Test` step in `build.yml` passes on all three OS legs (windows-latest, ubuntu-latest,
  macos-latest) with the new project wired in.
- At least 3 real unit tests exist and pass, covering the scenarios listed above (or equivalent existing
  behavior if those specific methods have since changed).

## Suggested commit message(s)

```
chore(theme): commit pending theme-rework changes
test: scaffold WorkLedger.Tests project and wire into CI
```
(Split into two commits if the theme changes and the test scaffolding are unrelated in intent.)

## Prompt for Claude Code

> Review the current git status and any uncommitted changes on this branch. Commit them with an appropriate
> Conventional Commit message, or tell me what's unclear before committing. Then scaffold a new xUnit test
> project at `Tests/WorkLedger.Tests/`, add it to `WorkLedger-Client.slnx`, reference the main project, and
> write a few real unit tests against existing stable behavior (e.g. `SettingsService`'s legacy
> `BackupInterval.OnClose` migration, `ValidateBackupSource`, and `LocalDbService.SaveProjectAsync`'s
> insert-vs-update convention). Confirm `.github/workflows/build.yml`'s existing `dotnet test` step picks up
> and passes the new project, including on all three OS matrix legs.
