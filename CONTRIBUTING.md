# Contributing

Thanks for contributing to VaultSync.

## 1) Planning Model
Use `VS-xxxx` as the default work-item format for planned engineering work.

ID model:
- `VS-xxxx` (default): roadmap, implementation, test, release execution
  - release family by hundreds (example: `15xx` for `1.5`)
- `ISS-xxxx` (optional): grouped issue/UX cleanup batches in changelog-only tracking
- `BUG-xxxx` (optional): explicit bug-fix IDs in changelog tracking
- `REL-xxxx` (optional): release-gate follow-up tracking

Rules:
- For planned feature work, always create/use `VS-xxxx` in `ROADMAP.md`.
- Keep one primary scope per ID.
- Reuse the same ID in PR description, validation notes, and changelog entry when applicable.

## 2) Before You Start
- Check `ROADMAP.md` and open issues.
- Confirm acceptance criteria before coding.
- For risky or cross-cutting changes, align scope first.

## 3) Development Setup
1. Install .NET 8 SDK.
2. Restore:
   `dotnet restore`
3. Run UI:
   `dotnet run -f net8.0-windows10.0.19041.0 --project src/VaultSync.UI/VaultSync.UI.csproj`

## 4) Implementation Rules
- Keep changes focused.
- Avoid unrelated refactors in the same PR.
- Keep heavy operations off the UI thread.
- Add or update tests for behavior changes.
- Prefer localization keys over hardcoded UI text.

## 5) Documentation Rules
When behavior changes, update in the same PR:
- `docs/wiki/*` and/or `docs/HELP.md`
- `CHANGELOG.md`
- `ROADMAP.md` if ticket state/scope changed
- `docs/WHATS_NEW.md` for user-facing release highlights

Use `docs/README.md` and `DOCUMENTATION.md` as structure references.

## 6) Pull Request Requirements
- Reference the related ID (`VS-xxxx` preferred).
- Describe what changed and why.
- List compatibility/migration impact if any.
- Include validation evidence (build, tests, manual checks).

## 7) Quality Gates
Run before requesting review:
- `dotnet build VaultSync.sln`
- relevant tests for touched areas
- smoke check of impacted UI flows if UI changed

## 8) Release Hygiene
- Ensure changelog entries are categorized (`Added`, `Changed`, `Fixed`).
- Ensure docs and localization are updated for new user-facing behavior.
- Ensure roadmap states are accurate for completed/remaining work.

## 9) Reporting Bugs
Use `docs/wiki/Reporting-Bugs.md`.
Security issues: follow `SECURITY.md`.
