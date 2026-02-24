# VaultSync Documentation

This file is the canonical documentation hub for VaultSync.

For a quick file index, see `docs/README.md`.

## 1. Product Summary
VaultSync is a cross-platform snapshot and backup app for project folders.

Core pillars:
- Snapshot: capture project state.
- Backup: write snapshot content to one or more destinations.
- Verify: validate backup integrity.
- Sync metadata: merge backup history across machines.

## 2. Documentation Structure

### 2.1 Top-level docs
- `README.md`: public product overview and install entry point.
- `ROADMAP.md`: planned and completed work by release.
- `CHANGELOG.md`: shipped and unreleased change history.
- `CONTRIBUTING.md`: contribution, planning, and quality gates.
- `SECURITY.md`: vulnerability reporting and supported versions.

### 2.2 Operational docs
- `docs/HELP.md`: in-app help target and concise user guidance.
- `docs/RELEASING.md`: release packaging/publishing flow.
- `docs/UPDATER.md`: patch asset contract and update flow.
- `docs/WHATS_NEW.md`: user-facing release highlights.

### 2.3 Wiki docs
- `docs/wiki/Home.md`: wiki entry page.
- `docs/wiki/*`: task and feature guides (installation, backups, destinations, troubleshooting, etc.).

## 3. Work-Item and ID Conventions
Primary planning IDs use `VS-xxxx`.

Additional prefixes can be used in changelog triage blocks when needed:
- `ISS-xxxx`: issue bundle (cross-cutting UX/doc/cleanup work)
- `BUG-xxxx`: bug fix tracking item
- `REL-xxxx`: release-gate follow-up item

Rules:
- `VS-xxxx` remains the default for roadmap planning and implementation tracking.
- In changelog entries, prefer `VS-xxxx` for all `Added` items (including backfilled/non-roadmap additions).
- For changelog-only `ISS/BUG/REL` IDs, use version-family numbering:
  - `1.0.x` -> `10xxx` (example: `ISS-10001`)
  - `1.1.x` -> `11xxx`
  - `1.2.x` -> `12xxx`
  - `1.3.x` -> `13xxx`
  - `1.4.x` -> `14xxx`
  - `1.5.x` -> `15xxx`
- If `ISS/BUG/REL` is used in changelog entries, define the scope clearly in the same release section.
- Do not mix unrelated scopes under one ID.

## 4. Update Policy for Docs
When changing behavior, update all relevant artifacts in the same PR:
1. User docs (`docs/wiki/*` and/or `docs/HELP.md`)
2. Release notes (`CHANGELOG.md`, `docs/WHATS_NEW.md`)
3. Planning state (`ROADMAP.md`) when scope/status changed
4. Localization keys if UI text changed

## 5. Role-Based Reading Paths

### Maintainer release flow
1. `ROADMAP.md`
2. `CHANGELOG.md`
3. `docs/RELEASING.md`
4. `docs/UPDATER.md`

### Contributor feature flow
1. `CONTRIBUTING.md`
2. `ROADMAP.md`
3. relevant wiki page under `docs/wiki/`
4. `CHANGELOG.md`

### End user help flow
1. `docs/HELP.md`
2. `docs/wiki/Quick-Start.md`
3. `docs/wiki/Troubleshooting.md`
4. `docs/wiki/FAQ.md`

## 6. Quality Checklist (Docs)
Before merging documentation updates:
- Links resolve to existing files.
- Terms are consistent (Snapshot, Backup, Destination, Keep, Imported, Full, Incremental).
- New settings/UI labels referenced in docs match localization keys in `Localization/strings.en.json`.
- Versioned notes align with `CHANGELOG.md`.

## 7. Related References
- Full docs index: `docs/README.md`
- Wiki sidebar: `docs/wiki/_Sidebar.md`
