# 1.9.0 implementation review — 2026-10-07

Status: **proposed; no approval recorded**. Owning review: PR #736.
The delivery order in [the release contract](RELEASE_1.9.0.md) requires joint
VS-1910/1917/1918/1919 review before disk capture and restore implementation.
This page summarizes that review; the linked contracts retain the detailed rules.

| Decision | Proposed first boundary | Still requires design or qualification |
| --- | --- | --- |
| VS-1910 / #501: routes | Typed Protect, History, Recover and Manage intents, with an explicit adapter preserving all eight existing pages | Coordinator, resource resolution, persistence policy, entry-point inventory and Windows/macOS/Linux UI parity. The pure mapping prototype is tested but not wired into navigation. |
| VS-1917 / #574: image | Full offline self-contained logical byte coverage, bounded records and payload integrity, durable completion/checkpoints and independent validation | Exact wire encoding, numeric input limits, integrity-suite selection, permanent valid/invalid fixtures and reader/writer implementation. No extension or interoperable format is established. |
| VS-1918 / #575: engine | Internal full raw-block engine in an isolated worker; Linux x64 offline recovery for candidate GPT/UEFI ext4/NTFS, 512/4096-byte sectors | Typed privilege/authorization protocol, device adapters, feasibility/fault testing, boot media and complete measured recovery loops. All four profiles remain unqualified. |
| VS-1919 / #576: identity/evidence | Opaque identity plus generation/content binding; measured evidence, simulations and confirmations remain distinct | Final schemas, bounded dependency evaluation, freshness rules, correlated failure handling and permanent fixtures. No readiness evaluator is implemented. |

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
