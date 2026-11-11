# Disk recovery: the user journey in 1.9.0

Owners: VS-1910/#501 (navigation), VS-1904/#492 (capture), VS-1906/#494
(media), VS-1907/#495 (restore), VS-1919 (identity), VS-1909 (qualification).
Clarified with the maintainer on 2026-11-06. This document separates delivered
components from the intended product journey; it does not qualify disk support.

## What an ordinary user can do today

The desktop Disk Cloning page can select a folder and request read-only image
verification on Linux x64. Windows and macOS retain the page but require the
Linux recovery environment for this verifier. The desktop cannot launch the
privileged recovery host, create qualified boot media, capture its running system
disk, or authorize restoration.

The installed recovery controller and capture console currently run in a
separately assembled Linux recovery installation. Capture uses the physical
console, a kernel-read-only offline source and preprovisioned storage on a
different physical disk. Restore uses a graphical controller and independent
physical overwrite confirmation. The validation VM is a laboratory installation,
not a bootable download or an ordinary-user setup procedure.

Consequently, the complete user journey is not delivered yet. Neither a visible
wizard nor a passing component test means that a user can recover a computer.

## Product journey to complete before declaring it usable

The desktop should guide three clearly separated tasks:

1. **Create a disk image.** Explain required offline capture and storage space,
   select a source and storage, prepare the recovery environment, capture and
   verify the image, and show its result and how to use it. The initial full raw
   image includes unused sectors and potentially deleted data.
2. **Prepare recovery media.** Choose a disposable removable device with an
   explicit erase review, produce and verify a bootable package, and explain how
   to boot it. Media creation and its platform/firmware qualification remain open.
3. **Restore a disk.** Discover available recovery copies, select and verify one,
   choose the actual destination, show what will be overwritten, obtain fresh
   physical confirmation, restore with visible progress, verify the result and
   boot the recovered system. A typed filesystem path must not be the normal
   image-selection interface. Keep manual selection as an advanced escape hatch.

Do not present disconnected buttons as completed tasks. Show availability and
the actual next step, preserve cancellation and give actionable failures.
The image picker must distinguish a discovered candidate from a verified image;
unverified filenames, lengths or labels do not prove integrity or bootability.

## Starting from the desktop and restarting

An app-first experience can prepare recovery intent and guide a restart into an
independent environment. It still requires that environment; a reboot does not
make overwriting the live system disk safe. Current capture/restore contracts
require offline, unmounted devices and independent boot admission.

Automatic boot selection and a durable desktop-to-recovery intent handoff are
not implemented. Their per-platform boot, signing and recovery contracts need
implementation and measurement before enabling them. A prepared plan may help
select an image after boot, but must never transfer disk-write authority: image,
device identity, current observations and physical overwrite consent are checked
again in recovery. Retain an independently bootable recovery path when the
installed OS or application cannot start.

Do not promise that the entire operation is applied silently at restart. The
user must see the fresh destination review before a destructive operation.

## Will VaultSync still be installed afterwards?

A full raw restore returns the captured disk contents. VaultSync binaries,
configuration and database return if they were on that disk at capture time.
Installation or changes made after the snapshot do not survive an overwrite.
An image created without VaultSync does not acquire it through the current
restore implementation. Files on disks outside the image are outside that claim.

A universal post-restore installer, setup merge and first-boot continuation are
not implemented. If added, they need an explicit product contract and separately
verified installation/reconciliation phase. Never inject the recovery operator's
configuration, account, secrets or executable into the recovered system silently.
Do not change restored bytes and still report an unchanged byte-for-byte result.

Raw cloning also copies VaultSync's persisted installation identity. Recovering
the same machine and creating a second independent machine need distinct identity
handling before both copies can write to the same backup destination. Windows
DPAPI, macOS Keychain and Linux Secret Service protect saved credentials; restoring
disk bytes does not guarantee that secrets unlock on different hardware/accounts
or that remote credentials remain valid. Preserve recoverable data and request
reauthorization where required instead of promising portable secrets.

## What "verified" means after restoring — 2026-11-11

Follow [the independent evidence claims](RECOVERY_EVIDENCE_SCOPE_1.9.md).
Discovery, digest verification, destination read-back and restored-system
cold boot are distinct visible outcomes; **boot does not certify application
or database correctness**. Show the actual environment, bytes/properties
tested, time and unsupported/untested conditions. A successfully restored
system may still need version-specific service, account, encryption, hardware
driver or application checks. Describe optional such tests only when actually
implemented and run; never infer them from an intact image or a passing boot.

A GUI desktop flow must not automatically boot arbitrary recovered images or
execute scripts simply to claim higher confidence. The 1.9.0 qualification VM
is a lab tool; optional bounded, consented recurring VM drills belong to 1.9.5,
and richer presentation of the scopes belongs to 1.9.4. The installed 3 GiB
Debian loop is a specific observed fixture, not universal supported recovery.

## Beta acceptance

Every beta retains Windows, macOS and Linux desktop support. Only disk support is
staged: Linux first, then Windows, then macOS. Each advertised disk profile needs
the connected ordinary-user workflow and the measured
Capture → Validate → Boot → Restore → Validate loop, including booting the
restored OS. App-first reboot automation is not a substitute for that evidence.

See [the beta program](BETA_PROGRAM_1.9.0.md),
[installed entry](RECOVERY_ENTRY_1.9.md),
[capture/export](RECOVERY_CAPTURE_1.9.md) and
[recovery UI](DISK_RECOVERY_UI_1.9.md).
