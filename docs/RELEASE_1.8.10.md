# VaultSync 1.8.10 — Security and Dependency Maintenance (candidate)

This is a **pre-release preparation**, not a published update or announcement that any unverified fix has shipped.

For the mandatory branch policy, independent test evidence and complete
release gate, use [engineering workflow](ENGINEERING_WORKFLOW.md). No public
advisory disclosure is authorized by adding a backport or passing a build.

## Source and isolation

- Source: published `v1.8.9` stable line, branch `release/1.8.10` created from `Stable`.
- Scope: serviced .NET 10 LTS runtime/SDK packages, qualified dependency updates, and isolated regressions verified on the exact shipped 1.8.9 code baseline.
- Source release identity: `1.8.10`; previous stable: `1.8.9` (2026-09-16).
- Target planning date: 2026-11-14, not a commitment to release without evidence.
- No 1.9 recovery features, disk-cloning APIs, format migrations, or unrelated behavior changes.
- Do not add embargoed vulnerability details to public commits, logs, CI, or issue discussions. Coordinate private reports under SECURITY.md.

## Scoped, not-yet-qualified 1.9.0 bug backports

Each of the three production files was **blob-identical** across the published
`v1.8.9`, `Dev`, and this candidate *before* its update. The backports are
scoped to old behavior, not the wider 1.9 implementation:

- `BUG-19001`: refuse `remove-project --quiet` and redirected stdin unless
  `--yes` is present, before touching the database; keep normal interactive
  confirmation and explicit opt-in deletion.
- `BUG-19027`: deserialize existing preset index property names with
  case-insensitive JSON matching; do not import 1.9 preset-store redesigns.
- `BUG-19029`: resolve macOS nested SMB destination paths to their verified
  share-mount root before unmount; reject mere sibling-prefix matches.

- `BUG-19047` / Refs #843: retain rsync exclusion-file cleanup ownership
  through failed process startup and normal exits, with a test-owned path
  instead of global temporary-directory scanning. Backported narrowly from
  [Ameen's PR #849](https://github.com/ATAC-Helicopter/VaultSync/pull/849);
  contributor credit remains `@ameenalqershi`.

The changes have targeted disposable regression tests. They remain pending
independent **exact-SHA** Linux/Windows/macOS build/test validation and the
native macOS nested-SMB manual test. The independent Fedora test of PR #849 reported 3/3 passing cases but an
MSBuild output-file collision and nonzero build exit. Do not treat this as
complete until a separate serialized exact-head build/test succeeds or the
release CI independently verifies the same backported change. The Draft
filter-refresh PR
#850 (`BUG-19048`) is not imported. Private advisory work stays isolated.

## Optional community entry

The What's New footer and Settings > Advanced have a small link to the
existing official Discord invitation (`https://discord.gg/sgP8UzeAQt`).
Participation is optional; no telemetry, login requirement or notification is
added. GitHub Issues and the private security advisory remain authoritative
for tracked bugs and vulnerabilities. Check cross-platform browser opening,
including failure if no browser is available.

## Pending release gates

- [ ] Review all changed dependency lock/asset graphs on Windows, Linux and macOS; verify package availability.
- [ ] Qualify SkiaSharp 4.156.0 / HarfBuzzSharp 14.4.0.100 as a complete cross-platform managed/native group, including rendering, font shaping, graphics fallback, RAW/DNG codec expectations and binary ABI tests; revert the rendering group if any platform fails.
- [ ] Run targeted backup safety, verification, restore, updater and metadata regression tests with disposable fixtures.
- [ ] Validate final self-contained packages embed only serviced Microsoft runtimes, including optional framework references, at >=10.0.15.
- [ ] Verify updater's allowed 1.8.9 predecessor and full-installer fallback; do not infer patch compatibility from version strings alone.
- [ ] Perform quality/security gates and license/SBOM/package provenance verification on exact source SHA.
- [ ] Run release-candidate packages on all three OS platforms with rollback and smoke tests.
- [ ] Preserve protected review and final Stable-promotion/signing workflows; never claim release qualification from a passing build alone.
- [ ] Coordinate disclosure of any private vulnerability only after remediation and publication approval.
