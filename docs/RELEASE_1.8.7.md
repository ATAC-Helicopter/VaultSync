# VaultSync 1.8.7 — Trust and Portability

This is the maintained implementation-status page for the active `1.8.7`
release. The canonical feature scope and acceptance criteria remain in
[`ROADMAP.md`](../ROADMAP.md#187--trust-and-portability).

## Release identity

| Field | Value |
|---|---|
| Current stable | `1.8.6` (`v1.8.6`, released 2026-08-10) |
| Active target | `1.8.7` |
| Planning started | 2026-08-12 |
| Stable target | 2026-10-30 |
| Working branch | `release/1.8.7` |
| Integration branch | `Dev` |
| Stable branch | `Stable` |
| Release PR | [#546](https://github.com/ATAC-Helicopter/VaultSync/pull/546) |
| Tagline | *Show the proof.* |

The release branch accumulates the qualified 1.8.7 work. `Dev` is the
integration branch; `Stable` represents shipped releases only. A beta is not
assumed and must be approved explicitly if the release needs one.

## Status as of 2026-08-15

### Implemented on the release branch

- The .NET SDK is pinned to `10.0.303` and the supported runtime baseline is
  `10.0.11`.
- Coordinated Microsoft runtime packages are pinned to the serviced baseline.
- CI audits a real self-contained publish and release jobs validate the runtime
  embedded in every supported RID.
- The permanent `Dev` branch was restored at the `v1.8.6` Stable commit and
  automatic head-branch deletion was disabled.
- The durable installation-identity provider is implemented and tested. It
  creates one atomic owner-private identity, rejects malformed or linked
  identity files, and remains separate from telemetry and host name. Production
  metadata writers now use it as their lease owner while retaining host name as
  a diagnostic label only.
- The repository lease primitive is implemented and tested in a separate
  coordination database: atomic acquisition, busy/read-only inspection,
  automatic heartbeat, conservative expiry, nonce-bound release, explicit
  stale takeover, and exceptional takeover evidence.
- Every existing portable-metadata writer now requires lease ownership:
  project settings, backup/history exports, all tombstone paths, deferred writes,
  and deferred flushing. Import and preview remain readable while busy and
  suppress optional source writes. Deferred stores flush only into an empty
  destination; divergent destination metadata is preserved for merge review
  instead of being overwritten. No UI takeover control is exposed yet.
- The canonical release-manifest v1 schema, deterministic generator, artifact
  classifier, exact size/SHA-256 verification, and complete platform-matrix
  gate are implemented. Release automation generates the manifest only after
  all direct-download platform artifacts have been built and collected. The
  post-publish gate and desktop updater consume the same schema; the updater
  rejects a release when its manifest identity or any GitHub asset name, URL,
  size, or digest disagrees.
- Snapshot Explorer, metadata-import review, and updater windows now use the
  current compact, theme-aware utility layout (`BUG-18103`).
- Built-in development presets now cover current generated caches and local IDE
  state without relying on unsupported negation rules; Windows Robocopy also
  consumes the shared preset resolver. Live `.git` internals remain excluded
  pending the separately gated full-repository mode (`BUG-18104`, `VS-1801`).
- Metadata-import previews now deduplicate portable metadata and corresponding
  legacy repository folders, count each proposed change once, and remain
  repeatable without mutating the local repository (`BUG-18105`).
- macOS SMB mount errors now normalize the complete credential-bearing share
  identity before masking any remaining raw or escaped password text, producing
  clean credential-free diagnostics (`BUG-18106`).
- Metadata import now exports snapshot tombstones only for snapshots it actually
  deletes; snapshots retained by local backups and unknown remote-only IDs are
  no longer advertised as deleted (`BUG-18107`).

These changes are not shipped until the release work reaches `Stable`.
Dependabot can therefore continue to report the old default-branch runtime
until promotion; that is a branch-state difference, not an unaddressed release-
branch package.

### In progress next

1. Surface repository status and an explicit stale-takeover decision in UI.
2. Make metadata imports previewable, versioned, durable, and reversible.
3. Expose the running build and canonical release identity through the app,
   CLI, diagnostics, and support exports.
4. Exercise two-machine, disconnect, clock-skew, and representative NAS/SMB
   behavior before enabling supported concurrent-machine workflows.
5. Reduce codebase duplication and oversized orchestration through shared,
   regression-tested primitives without combining genuinely different platform
   behavior.

### Still planned

- per-platform SBOMs and supported build provenance;
- checksummed Recovery Evidence Packages;
- allowlisted, reviewable support bundles;
- synchronized public release metadata;
- standardized retry, path, serialization, lifecycle, dialog, and projection
  infrastructure plus review of every remaining duplicated block;
- full repository and emergency-recovery documentation after schemas stabilize;
- final localization, theme, accessibility, static-analysis, dependency, and
  cross-platform release qualification.

## Safety contracts

### Distribution trust

- Every published asset must have an exact byte size and SHA-256 digest in one
  machine-readable manifest generated from the final artifact.
- An unavailable digest, inconsistent version, or unexpected asset must fail
  release validation.
- Unsigned direct downloads must never be described as signed or notarized.

### Repository writing

- Installation identity must be durable, random, local, and independent of a
  mutable host name.
- A repository lease must identify owner, operation, nonce, application
  version, acquisition time, heartbeat, and expiry.
- Read-only inspection remains possible while a valid writer exists.
- Stale takeover must be explicit and leave diagnostic evidence.
- Pre-1.8.7 clients do not understand the lease protocol and cannot safely
  cooperate as concurrent writers.

### Cross-machine metadata

- Imports must be preview-only until destructive or conflicting changes are
  explicitly accepted.
- Machine-local secret references cannot be silently applied elsewhere.
- Non-overlapping portable edits may merge; overlapping edits must show base,
  local, and remote values with writer and timestamp provenance.
- Keep local and accept remote must create durable resolutions so the same
  unchanged conflict does not return.
- A confirmed merge can be undone until a later repository write supersedes it.

### Evidence and support exports

- Exports use an allowlist, not a denylist alone.
- Credentials, tokens, plaintext passwords, encryption secrets, and unrestricted
  local paths are forbidden.
- Users see exactly what will be included before the archive is created.
- Evidence packages are versioned, deterministic, checksummed, and readable
  without VaultSync.

## Definition of done

1. Every P0 roadmap item and confirmed P0 defect is complete.
2. Behavior, executable schema tests, user documentation, and release notes
   agree.
3. Upgrade and recovery exercises start from an unmodified 1.8.6 installation.
4. Two machines and representative NAS/SMB storage pass writer, expiry,
   conflict, clock-skew, interruption, and read-only inspection scenarios.
5. Windows, macOS, Linux, all maintained translations, themes, accessibility,
   SonarQube, CodeQL, and dependency gates pass.
6. An unpublished stable candidate produces and validates every expected asset
   and public metadata consumer before promotion.

## Maintainer links

- [Roadmap](../ROADMAP.md#187--trust-and-portability)
- [Release procedure](RELEASING.md)
- [Repository formats](REPOSITORY_FORMATS.md)
- [Cross-machine safety](CROSS_MACHINE_SAFETY.md)
- [Metadata sync](wiki/Metadata-Sync.md)
- [Security policy](../SECURITY.md)
- [Updater contract](UPDATER.md)
