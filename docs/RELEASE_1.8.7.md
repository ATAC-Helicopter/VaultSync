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
| Stable target | 2026-08-24 |
| Working branch | `release/1.8.7` |
| Integration branch | `Dev` |
| Stable branch | `Stable` |
| Release PR | [#546](https://github.com/ATAC-Helicopter/VaultSync/pull/546) |
| Tagline | *Show the proof.* |

The release branch accumulates the qualified 1.8.7 work. `Dev` is the
integration branch; `Stable` represents shipped releases only. A beta is not
assumed and must be approved explicitly if the release needs one.

## Delivery timeline

`1.8.6` shipped on 2026-08-10. The two-week minor-release ceiling therefore
sets 2026-08-24 as the Stable deadline for `1.8.7`.

| Window | Focus |
|---|---|
| 16–19 August | Complete versioned, durable cross-machine conflict handling. |
| 20–21 August | Finish build identity, SBOM/provenance, evidence, and support-export contracts. |
| 22 August | Finish repository documentation, public metadata synchronization, and bounded cleanup. |
| 23 August | Run the unpublished stable candidate, upgrade, two-machine, NAS/SMB, localization, theme, accessibility, and security gates. |
| 24 August | Merge through `Dev` to `Stable`, publish, verify assets, and close the milestone. |

P0 safety defects cannot roll forward. Non-blocking P1 polish may move to
`1.8.8` instead of extending this deadline. Minor releases do not require a
beta; major releases use explicit beta rounds once their feature train is
complete and stable enough for broader qualification.

## Status as of 2026-08-20

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
  stale takeover, and exceptional takeover evidence. Settings can inspect each
  destination's current writer, show its host, short durable identity,
  operation, version, heartbeat, and expiry, and require an explicit two-step
  confirmation before replacing the exact inspected stale nonce. The displaced
  lease is retained as evidence, and the UI warns that pre-1.8.7 clients do not
  participate in this protocol.
- Every existing portable-metadata writer now requires lease ownership:
  project settings, backup/history exports, all tombstone paths, deferred writes,
  and deferred flushing. Import and preview remain readable while busy and
  suppress optional source writes. Deferred stores flush only into an empty
  destination; divergent destination metadata is preserved for merge review
  instead of being overwritten.
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
- Canonical and platform patch manifests now use an immutable, digest-verified
  on-disk cache, preventing routine update checks and application restarts from
  inflating GitHub JSON asset download counts (`BUG-18108`).
- Disposable local storage now has explicit age and size limits for diagnostics,
  logs, caches, updater artifacts, and abandoned temporary work. Managed macOS
  mount paths also fail closed unless SMB or NFS is still mounted, preventing
  remote backup payloads from falling through onto the system drive
  (`BUG-18109`). See [Local storage and cleanup](STORAGE_HYGIENE.md).
- Roadmap-to-GitHub synchronization now reconstructs wrapped ticket contracts,
  preserves manually maintained issue bodies, constrains file inputs to the
  repository, validates every remote identifier, and provides an exact
  structured dry run before any Project or issue write (`BUG-18098`).
- Cross-machine project settings now use durable per-source merge bases and a
  field-level three-way planner. Independent local and remote edits merge
  automatically; only overlapping fields require review. Conflict records
  retain source/base revisions and both decisions preserve non-overlapping
  work before advancing the durable base (`VS-1879`).
- Every portable project writer now advances only the exact revision it
  inspected. Schema-version-3 rows carry their base revision, per-field writer,
  revision and timestamp provenance, plus the latest safe resolution evidence;
  stale writes roll back without replacing remote metadata (`VS-1879`).
- Conflict review now presents Base/local/remote values with revision, writer,
  and timestamp context. The latest decision can restore the previous local
  state until the next portable repository write supersedes that undo record;
  all six undo strings ship in every maintained locale (`VS-1879`).
- The running build now exposes one schema-versioned identity in Settings,
  startup diagnostics, support bundles, recovery reports, and
  `vaultsync --version --json`. Release artifacts stamp their channel, commit,
  package, update source, official status, and honest signature state; missing
  facts remain `unknown` and incomplete builds cannot claim official status
  (`VS-1871`).
- Every final direct package now receives a validated SPDX 2.3 SBOM tied to its
  canonical-manifest SHA-256 and platform-specific resolved dependency graph.
  GitHub signs provenance for the final package bytes and an SBOM attestation
  for each package; release-candidate automation verifies one package both
  online and from a downloaded bundle plus trusted-root snapshot (`VS-1873`).
- Recovery now exports a portable evidence ZIP containing canonical JSON, a
  readable Markdown report, a versioned manifest, and a standard SHA-256 index.
  Stable semantic digests make equivalent evidence comparable; pseudonymous
  repository identities and redacted paths avoid exposing raw local layout.
  Canonical confidence rows retain measured basis, state, codes, observation
  times, and whether encrypted recovery-point evidence exists.
  Validation rejects tampering, missing or duplicate files, unsafe paths,
  unexpected content, and unsupported schemas (`VS-1874`).
- Recovery confidence and evidence labels, repository-writer controls, History
  evidence events, backup-widget status, folder pickers, backup-location
  feedback, verification failures, and restore progress now use the localization
  contract in every maintained language. Recovery Inspector evidence uses
  flexible cards instead of fixed columns so longer translations remain
  readable (`BUG-18110`).
- Recovery exports no longer fall back to a publicly writable temporary
  directory, manual cache clearing stays within private application roots, and
  destination cleanup now retains every successful mount across credential
  retries. Unreachable metadata, restore, and update branches identified by
  Sonar were removed (`BUG-18111`).
- Passive destination health, cleanup, metadata, and history work no longer
  unlocks macOS Keychain or mounts an SMB share. Credentials are requested only
  by an explicit destination test or a real backup (`BUG-18112`).
- macOS launch-on-login now writes to `~/Library/LaunchAgents`, removes the
  erroneous entry under Documents, and avoids launch-time process kickstarts
  (#559). Both architecture-specific DMGs now contain a canonical
  `/Applications/VaultSync.app` with synchronized bundle metadata. The exact
  1.8.6 predecessor receives a one-time architecture-aware bridge patch that
  stages, signs, verifies, and launches the canonical bundle before retiring
  the legacy app; older versions use the full DMG. Future bundle-root patches update and verify
  `Info.plist` with the runtime payload (#560, #561).

These changes are not shipped until the release work reaches `Stable`.
Dependabot can therefore continue to report the old default-branch runtime
until promotion; that is a branch-state difference, not an unaddressed release-
branch package.

### In progress next

1. Complete the two-machine, disconnect, clock-skew, and NAS/SMB qualification
   matrix for versioned metadata merging.
2. Run the release-candidate supply-chain job to qualify its online and offline
   attestation checks against real final packages.
3. Reduce codebase duplication and oversized orchestration through shared,
   regression-tested primitives without combining genuinely different platform
   behavior.

### Still planned

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
