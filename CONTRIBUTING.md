# Contributing

Thanks for helping improve VaultSync. This guide defines the default workflow for planning, implementation, and release-ready contributions.

## 1) Planning Model 
- Use `VS-xxxx` as the default work-item ID format for all planned work.
- ID rules:
  - `VS` = VaultSync work item.
  - First two digits map to release family (`15xx` -> `1.5`, `16xx` -> `1.6`).
  - Last two digits are sequence numbers in that release stream.
- Every non-trivial change should map to a `VS-xxxx` item in `ROADMAP.md`.
- Use the same ID in PR title/description, commit messages (when applicable), and QA references.
- In `CHANGELOG.md`, include `VS-xxxx` IDs for actual feature work (Added/Changed/Fixed tied to roadmap features).
- For pure housekeeping/doc-only/test-only cleanup, IDs are optional unless the change maps to a planned roadmap item.

## 2) Before You Start
- Review `ROADMAP.md` and open issues to avoid duplicate work.
- For large or risky changes, open a discussion first and align on scope.
- Confirm acceptance criteria before coding.

## 3) Development Setup
1. Install .NET 8 SDK.
2. Clone the repository and restore:
   `dotnet restore`
3. Run the UI app:
   `dotnet run -f net8.0-windows10.0.19041.0 --project src/VaultSync.UI/VaultSync.UI.csproj`

## 4) Implementation Rules
- Keep changes focused; avoid mixing unrelated refactors in the same PR.
- Prefer clear code over clever code.
- Avoid unnecessary allocations in hot paths.
- Keep heavy work off the UI thread.
- If behavior changes, add or update tests where practical.

## 5) Pull Request Requirements
- Include the related `VS-xxxx` ID in the PR title or description.
- Explain what changed, why, and any migration or compatibility impact.
- Include validation evidence (build/test/manual checks).
- Keep PRs small enough for practical review when possible.

## 6) Release Hygiene
- Update `CHANGELOG.md` for all user-facing changes.
- Update documentation in `docs/wiki/` when behavior, workflows, or troubleshooting changes.
- Add localization keys for new UI text and keep language files in sync.

## 7) Quality Gates
Run these before requesting review:
- `dotnet build VaultSync.sln`
- Relevant tests for touched areas
- Smoke-check core UI flows if UI code changed (startup, backup, restore, settings)

## 8) Reporting Bugs
Use the format in `docs/wiki/Reporting-Bugs.md`.
