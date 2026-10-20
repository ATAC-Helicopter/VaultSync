# 1.9.0 implementation review — 2026-10-07

Current direction: **approved by the maintainer on 2026-10-20**;
[retained joint review](release-evidence/1.9-joint-architecture-approval-2026-10-20.md).
Implementation/security finalization and all support/release qualification remain open.
Earlier proposed statuses below are historical.


Historical status on 2026-10-07: **proposed; no approval recorded**. Owning review: PR #736.
The delivery order in [the release contract](RELEASE_1.9.0.md) requires joint
VS-1910/1917/1918/1919 review before disk capture and restore implementation.
This page summarizes that review; the linked contracts retain the detailed rules.

| Decision | Proposed first boundary | Still requires design or qualification |
| --- | --- | --- |
| VS-1910 / #501: routes | Typed Protect, History, Recover and Manage intents, with an explicit adapter preserving all eight existing pages | The page-only coordinator now drives existing shell pages with bounded Back/Forward and keyboard shortcuts. Linux native evidence covers named scenarios; API/lifecycle, resource routes, new persistence and full Windows/macOS/Linux parity remain open. |
| VS-1917 / #574: image | Full offline self-contained logical byte coverage, bounded records and payload integrity, durable completion/checkpoints and independent validation | The raw reader wire contract, SHA-256, explicit limits and independent fixtures are fixed in DISK_IMAGE_RAW_WIRE_1.9.md. Product writer/durable publication, staging, trusted worker and recovery qualification remain open. No public file extension is established. |
| VS-1918 / #575: engine | Internal full raw-block engine in an isolated worker; Linux x64 offline recovery for candidate GPT/UEFI ext4/NTFS, 512/4096-byte sectors | Typed privilege/authorization protocol, device adapters, feasibility/fault testing, boot media and complete measured recovery loops. All four profiles remain unqualified. |
| VS-1919 / #576: identity/evidence | Opaque identity plus generation/content binding; measured evidence, simulations and confirmations remain distinct | A single-observation applicability prototype tests binding, provenance and chronology. Final schemas, dependency evaluation, observation precedence, producer trust and correlated failure handling remain open. No integrated readiness evaluator is implemented. |

Contracts: [routes](ROUTE_ARCHITECTURE_1.9.md),
[image format](DISK_IMAGE_FORMAT_1.9.md),
[engine/support ADR](adr/001-imaging-engine-1.9.md),
[identity/evidence](RECOVERY_IDENTITIES_1.9.md).

The proposed first disk profile excludes live/mounted capture, Secure Boot,
encryption, macOS/APFS bare-metal recovery, RAID/LVM/multi-disk, resizing/sector
translation, foreign images, deltas and network payloads. Existing file backups
keep their current behavior. No disk/device operation is authorized by a route,
image descriptor, test fixture or this document.

After direction review, close the concrete schema/protocol decisions with bounded
regular-file fixtures and refusal/fault tests before implementing device access.
Device integration then needs its separate exact source/destination authorization
and retained measured Capture → Validate → Boot → Restore → Validate evidence.
Architecture approval would not qualify a device, supported platform or release.
Approval records must reflect an actual maintainer review; do not infer them from
an instruction to continue development or from passing CI.


## Implementation decision package — 2026-10-20

Current status: **joint implementation direction approved, 2026-10-20**.
[Actual maintainer review record](release-evidence/1.9-joint-architecture-approval-2026-10-20.md). This is the concrete
combined direction for VS-1910/#501, VS-1917/#574, VS-1918/#575 and VS-1919/#576.
The maintainer already delegated selection of the best engine approach. That
choice is reflected in the internal raw/offline recommendation below; this
package supplies the wider format, operation and identity decisions required
by this document's joint review gate. It does not manufacture an approval.

### Selected recommendation

1. **Recovery environment:** independently booted Linux x64/UEFI, offline and
   without Secure Boot. The first measured beta profile is GPT/ext4/512 in a
   disposable VM. GPT/NTFS and 4096-byte profiles remain candidates until their
   own runs qualify them. Existing desktop file backups retain their platforms.
2. **Engine:** internal full raw-byte capture and restore, behind a fixed trusted
   worker. No Partclone dependency, custom compression, live snapshots, resize,
   sector translation or filesystem allocation parser in this first profile.
   Copy all disk bytes, including unused regions; users must be told that raw
   images can retain deleted data and require protected storage.
3. **Image:** a self-contained directory with fixed `image.metadata` and
   `payload.raw` names, not manifest-selected paths. Keep the independently
   exercised 24-byte VSIMAGE 1.0 envelope, little-endian numeric fields,
   network-order UUIDs, SHA-256, schema-2 GPT/source/tool records and completion
   binding as the starting serialization. One full raw part/extent; no
   compression, encryption, external parents, URLs or executable descriptors.
   Freeze the complete byte specification and permanent fixtures before a
   product writer; synthetic records never become native-capture evidence.
4. **Bounds and completion:** stream fixed-size buffers, enforce numeric/count
   limits before allocation, and reject unknown required features. Define
   a first-profile hard ceiling of 8 TiB, 16 MiB metadata, 128 GPT entries and
   128 KiB buffers; these are refusal limits, not qualified capacity claims.
   Capture requires space for the entire raw payload and metadata; restore
   requires separate space for protected staging. Reject overflow before
   allocation or opening an output. Flush payload,
   manifest and directory state before publishing completion. An interrupted
   operation stays incomplete; automatic resume requires separate demonstrated
   source continuity and must otherwise refuse and require a new attempt.
5. **Privilege:** keep UI and hostile image inspection unprivileged. Before any
   write, authenticate the local worker channel and authorized operation, verify
   the worker build, re-observe exact native identity and geometry through the
   opened handle, prove supported offline/exclusive state, and resolve repository
   ancestry. The new inventory client is not this privileged boundary.
6. **Restore:** fully verify and protect staged payload bytes before writes.
   Require a whole-disk destination of equal capacity and logical geometry,
   outside the image repository, with an explicit translated overwrite preview
   and confirmation. Unknown, ambiguous, mounted, changed or unsupported
   destinations refuse. Record interrupted writes honestly; no rollback claim.
7. **Identity and evidence:** image/operation/generation/content root binding is
   distinct from device identity and trusted origin. Checksums prove recorded
   byte integrity, not authorship. Record actual measured stages, negative cases
   and producer/build/environment; never promote a fixture or user confirmation
   into successful recovery evidence. Redact physical identifiers from exports.
8. **Desktop integration:** extend the existing typed navigation and retained
   pages. Add the complete recovery workflow with all 19 locales, accessibility,
   cancellation and language-change handling; no resource CLI or later-release
   discovery/help scope is pulled into 1.9.0.

### What approval allows and what remains gated

Approval selects this joint implementation direction and allows device-adapter
implementation/testing in disposable VMs after the concrete protocol/format
security checks above. It does not authorize writes to maintainer host devices,
weaken required PR reviews, qualify support or publish a beta. Keep #736 draft.

A beta still requires all owned feature paths integrated, at least one measured
Capture → Validate → Boot → Restore → Validate profile, negative/refusal and
interruption tests, installed desktop/localization review, exact 1.8.9 updater
qualification, and full-inventory online/offline release provenance. Unsupported
profiles must be shown explicitly. The custom compression experiment stays
secondary. No release date or completed-feature claim is made by this package.

Record an actual joint review outcome, reviewer, date and retained reference in
this document and the engine matrix before qualifying a capture. If a direction
changes, update the four contracts and qualification plan together.

## Recorded outcome

The maintainer explicitly approved this combined package after its publication
in 484be54. The engine matrix now retains the named, dated and hash-bound review
record. This approves implementation direction; exact serialization/security
implementation and all qualification remain subject to the retained gates above.
No profile was promoted and no prior laboratory result became recovery evidence.
