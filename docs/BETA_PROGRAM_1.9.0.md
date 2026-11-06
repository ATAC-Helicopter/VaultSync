# VaultSync 1.9.0 staged disk beta program

Owner: VS-1918 / #575, with VS-1904–VS-1909 implementation and qualification.
Maintainer direction and clarification: 2026-11-05. Preparation remains
`work/1.9-roadmap-realignment`, sole draft PR #736 into Dev.

## Desktop support and staged disk support

Every beta keeps the existing Windows, macOS and Linux application support.
The stages below apply only to **disk cloning, system imaging and recovery**.
They do not make a whole desktop platform unsupported, remove its file-backup
features or change later 1.9.x release ownership. Direct-download assets remain
cross-platform; the maintainer excludes MSStore assets from this beta program.

| Milestone | Desktop application | New disk scope to qualify | Scope retained |
| --- | --- | --- | --- |
| 1.9.0-beta.1 | Windows, macOS, Linux | Linux systems on explicit GPT/UEFI ext4 profiles | Existing desktop/file-backup functionality |
| 1.9.0-beta.2 | Windows, macOS, Linux | Windows systems on explicit GPT/UEFI NTFS profiles | Qualified Linux disk scope |
| 1.9.0-beta.3 | Windows, macOS, Linux | macOS systems after an approved and measured APFS/boot implementation | Qualified Linux and Windows disk scope |

These are intended milestone names, not cut versions, promised dates or existing
releases. Repairs can require additional beta builds; never move a released tag
or reuse its version. Beta numbering must remain unique and monotonic. Each
published release states its exact measured profile list and unresolved limits.

The Linux and Windows candidates currently use the **offline Linux x64 recovery
environment**. A Windows source OS is distinct from running an elevated disk
worker inside Windows. Native live capture, snapshots, encryption and Secure
Boot are not added implicitly. macOS/APFS remains excluded until its own engine,
privilege, boot and recovery contracts and measurements are complete. In
particular, no current Linux worker is claimed to support Apple Silicon recovery.

## What must work before the first disk beta

Beta 1 must deliver the actual Linux user workflow: acquire an eligible offline
source, capture and verify a complete image, identify source/output/destination
safely, boot independent recovery media, restore with explicit exact-device
confirmation, then verify contents and boot the restored system. A lab harness
calling an otherwise unreachable Core API is not delivered capture functionality.
Pure planners, tiny GPT fixtures, unit tests and a visible page cannot replace
this installed product workflow or qualify an operating-system profile.

For every advertised profile, retain source-bound Capture → Validate → Boot →
Restore → Validate measurements and destructive-refusal/cancellation evidence.
At least one real Linux profile must be qualified for beta 1. Candidate profiles
remain candidates; unsupported combinations refuse before destructive work.
The existing executable qualification gate remains mandatory and unchanged.
Later betas add qualified profiles without dropping previously supported ones.
Physical hardware breadth remains a stable gate, separate from the explicitly
measured disposable-VM beta scope.

All stages also require real cross-platform desktop/package checks, predecessor
1.8.9 update/data-preservation checks, beta-channel discovery, exact-source
provenance and asset signatures, privacy/localization compliance, a passing
separate Sonar quality gate and protected release reviews. Neither the staged
schedule nor urgency waives these shared gates. The existing release workflow
and signing/updater names and schema remain authoritative. Do not merge #736
merely to make an updater discover a beta: follow RELEASE_SIGNING.md.

## Current execution order

1. Connect production offline capture to the admitted recovery entry. Preserve
   private captured originals; define a reviewed descriptor/export boundary so
   the nonroot controller can subsequently restore a product-created image.
2. Finish Linux graphical/independent-console handoff, source/output physical
   separation, cancellation and truthful terminal/cleanup behavior.
3. Execute a real Linux OS/profile loop and independent-media qualification in
   owned disposable guests with a bounded workspace; never use host disks.
4. Complete shared security/coverage, packages, updater and provenance gates;
   only then cut the exact beta identity and publish qualified assets.
5. Add Windows then macOS disk implementations and their measured profiles,
   keeping all three desktop applications supported throughout.

## Relationship to earlier requirements

This maintainer instruction supersedes the earlier requirement that **beta 1**
contain all operating-system disk implementations at once. It does not remove
1.9.0 features from final delivery, declare any profile qualified, relax data
safety, or reinterpret old evidence. All three disk stages remain 1.9.0 work;
compression research stays secondary and later 1.9.x features keep their owners.
