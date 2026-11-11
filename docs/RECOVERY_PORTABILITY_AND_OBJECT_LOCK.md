# Recovery portability and Object Lock qualification (planned 1.9.2–1.9.3)

**Status:** proposed acceptance contract for future releases. This is not shipped functionality, a compatibility promise for every backup, or evidence that any remote provider has passed qualification.

**Roadmap owners:** `VS-1931`–`VS-1934` for 1.9.2 Portable Recovery; `VS-1921`, `VS-1923`–`VS-1925` for 1.9.3 Offsite Protection. The 1.9.0 offline disk/boot recovery plan and its supported-system gates remain unchanged.

## Why this matters

A complete failure may destroy the original host, its local application database, configuration, saved credentials, and normal installation. A recovery plan cannot depend on those components surviving. An S3 Object Lock configuration is not proof of recovery either: retention protects particular object **versions** and can interact with ordinary delete markers, version selection, cleanup, and costs.

The goal is independently repeatable, evidence-backed recovery, not an unconditional claim that every image, repository, encryption setup, or S3-compatible endpoint is recoverable.

## 1.9.2: portable restore contract

A standalone, independently distributed restore utility must be usable on each **explicitly qualified** target OS without relying on the original host, installed VaultSync UI, its local SQLite application database, its stored configuration, or its OS credential store.

The supported clean-machine flow must:

1. Identify supported backup payloads and optional portable metadata on preserved read-only source media. Treat metadata as an accelerator, **not** as the only possible recovery route. A missing or corrupt portable metadata database must not make otherwise independently identifiable, supported payloads disappear.
2. Inventory candidate recovery points read-only, display format/version, encryption state, provenance, integrity coverage, and missing dependencies, and label unsupported or ambiguous payloads rather than guessing.
3. Allow an explicit, safe destination mapping and isolated restore preview before writing. The tool must never repair, delete, rewrite, or acquire a normal writer lease on the only surviving repository to make recovery possible.
4. Unlock supported encrypted payloads only with independently supplied, authorized recovery material. Existing machine-local key references are not credentials and cannot decrypt content on their own. If a required secret is lost, say recovery is blocked; do not imply the utility can bypass encryption.
5. Restore only supported, verified content to a separate target. Validate paths, links, size bounds, archive traversal, and output conflicts before materializing content. Report missing/corrupt blocks or files and partial results without a blanket pass.
6. Independently verify restored bytes against trusted available checksums, distinguish verified from unverified portions, and export a redacted, locally saved result with tool/build identity and evidence limitations.

### Published format and compatibility boundary

`VS-1933` must publish a reader-oriented, versioned specification for every supported backup format: layouts, naming/discovery rules, manifest placement, hash algorithm and scope, chunk/partition references where applicable, sparse/incomplete state, compression, encryption envelope/KDF descriptors, byte-order and path conventions, and compatibility examples/fixtures. Clearly separate **existing 1.8 folder/ZIP formats** from **future 1.9 disk images** and other planned formats.

`VS-1934` must define reader/write compatibility, supported historical versions, deprecation notice policy, forward-version rejection, migration without rewriting the only copy, and a deliberately read-only emergency recovery mode. A documented format alone does not establish support for every old, corrupt, or encrypted payload.

An emergency kit (`VS-1932`) should include independently verifiable binaries for supported platforms, hashes, offline instructions, format/reader version identification, and a checklist for preserving sources and carrying recovery credentials **separately and securely**. It must not embed credentials, passphrases, or local keychain exports by default.

### Required portable-recovery qualification

Use disposable fixtures and an isolated target. Disconnect or deliberately remove the original host and its local application database from the test environment before attempting recovery. Test at least:

- intact supported payload plus intact portable metadata;
- intact supported payload with missing and with corrupt portable metadata;
- incomplete, truncated, tampered, unsupported-version, and path-traversal payloads;
- supported encrypted restore with separately held recovery material, versus missing or incorrect recovery material;
- original path/drive absent, different destination path, output file conflict, and insufficient target space;
- clean-machine byte-for-byte restore and independently checked result manifest;
- crash/interruption at each destructive/write boundary, with repeatable, non-destructive restart behavior.

The release gate is a measured restore of representative supported datasets **without** the original application database, plus explicit failed/unsupported outcomes for cases that cannot safely be recovered. Unsupported combinations must be listed, not counted as passes.

## 1.9.3: object-storage and Object Lock qualification

S3-compatible storage does **not** automatically implement all Amazon S3 Object Lock semantics. Capability detection and qualification must be per provider and endpoint, with documented unsupported operations. Object Lock requires bucket versioning on Amazon S3; retention and legal holds attach to particular **versions**. Writing the same key can create another version; an unversioned delete request can create a delete marker without deleting a protected version. The apparent absence of a key in ordinary listing is not evidence its locked version vanished.

The offsite workflow must:

1. Upload complete, signed-or-hashed manifests and content with explicit committed/incomplete states; validate remote versions and checksums. A resumable or retried transfer must not publish incomplete backups as recoverable.
2. Record the exact object keys and, when supported/required, version IDs necessary for restore. Do not rely solely on a current-version listing to reconstruct a protected snapshot.
3. Explain effective retention mode (**governance** versus **compliance**), retention-until dates, legal holds, versioning, permissions, delete markers, and eventual lifecycle costs. Do not claim that "Object Lock enabled" means immutable recovery without checking the actual object versions.
4. Make retention cleanup, cancellation, repair, and overwrite operations retention-aware. For a protected version, preservation or an explicit failure is the correct outcome; a cleanup failure is not a reason to discard it, bypass protection, or conceal the failure.
5. Recover using the planned 1.9.2 independently usable restore path from an authorized remote copy onto a clean machine, with no reliance on the original host or its local database.
6. Preserve least privilege: ordinary backup/restore credentials should not require `s3:BypassGovernanceRetention` or permission to shorten/remove protected retention. Any exceptional administrative operation must be separate, reviewed, and explicitly out of the normal retention path.

### Required Object Lock test matrix

Run destructive tests **only in disposable, dedicated test buckets**. For each qualified provider/API combination, collect exact provider version, configuration, request status/errors, object-version and retention metadata, recovered checksums, and limitations:

| Scenario | Required observation |
|---|---|
| Versioning enabled; Object Lock absent or unsupported | Detect capabilities; no false immutability claim |
| Governance retention without bypass permission | Protected-version delete/overwrite-by-version is denied; normal recoverability remains |
| Compliance retention before expiration | Protected-version deletion is denied, including elevated actors where the provider guarantees this |
| Legal hold | Hold prevents protected-version removal independent of retention date |
| New upload using an existing protected key | Old protected version remains discoverable/restorable by version ID |
| Simple delete that creates a delete marker | Current listing can hide the object; restore locates the previous protected version |
| Retention expiration and subsequent cleanup | Only eligible versions can be removed; the scope and cost impact are previewed |
| Incomplete multipart transfer, retry, interruption | No premature "verified" point or silent loss; orphan cleanup respects protection |
| Remote manifest missing/corrupt; protected payload available | No invented metadata; bounded read-only discovery and explicit unsupported states |
| Clean-machine restore with original application database absent | Independent successful byte check of supported recovery point |
| Missing credential, missing encryption key, or unsupported provider | Explicit blocked/unsupported result; no success badge |

`VS-1924` owns the documented user-facing implications. `VS-1925` owns the clean-machine proof and must record what was actually restored, what was skipped, and why. Release readiness requires successful proof **and** negative tests, not a configuration screenshot or retention-only API success.

## Boundaries and release messaging

- **Current 1.8.x:** file/project backup, existing restore and evidence checks; no standalone 1.9.2 utility or native 1.9.3 S3 Object Lock support is claimed here.
- **1.9.0:** remains the staged offline disk capture, independent bootable environment, restore, and post-restore boot validation work for declared supported configurations. This contract adds no new 1.9.0 deliverable.
- **1.9.2 and 1.9.3:** planned, subject to test results, platform constraints, provider-specific qualification, and explicit compatibility matrices. Successful file-level recovery must not be represented as proven system boot recovery, and vice versa.

Reference: [Amazon S3 Object Lock documentation](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html). Amazon S3 behavior is a baseline for testing, not an assumption about every S3-compatible service.

This contract should be linked from the canonical [roadmap](../ROADMAP.md), [disaster recovery guidance](DISASTER_RECOVERY.md), and [repository format specification](REPOSITORY_FORMATS.md).