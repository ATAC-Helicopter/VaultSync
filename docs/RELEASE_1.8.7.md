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

## Status as of 2026-08-12

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
  identity files, and remains separate from telemetry and host name. Repository
  records do not consume it yet; lease integration is the next safety slice.

These changes are not shipped until the release work reaches `Stable`.
Dependabot can therefore continue to report the old default-branch runtime
until promotion; that is a branch-state difference, not an unaddressed release-
branch package.

### In progress next

1. Add repository lease parsing and read-only busy diagnostics.
2. Protect every metadata writer with durable identity and nonce ownership.
3. Make metadata imports previewable, versioned, durable, and reversible.
4. Generate the release manifest and expose complete build identity.

### Still planned

- per-platform SBOMs and supported build provenance;
- checksummed Recovery Evidence Packages;
- allowlisted, reviewable support bundles;
- synchronized public release metadata;
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
