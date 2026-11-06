# Installed offline capture — VS-1904 / #492

Status: implemented component, not yet native/profile qualification.
Preparation: PR #736, `work/1.9-roadmap-realignment`, 1.9.0.

## Entry and operator flow

Recovery media can select the fixed `capture` mode during staging. The producer
binds the capture unit and mode into the same immutable installation closure.
The internal RecoveryHost accepts only its fixed `--capture` role after full
installed-host admission. This is not a desktop elevation command; arbitrary
arguments, paths or an ordinary application launch do not grant disk access.
The existing headless/graphical restore entry and wire protocol remain separate.

The operator uses the actual VT1 console. It lists eligible offline read-only
whole disks, their capacities and sector geometry. Capture requires the exact
source name and fresh operation challenge; blank input cancels, Ctrl+C interrupts,
and the prompt expires. No source or destination name arrives from a network
request. The warning explains that raw images include unused sectors and deleted
data. The source remains kernel-read-only and is never opened for writing.

A fresh inventory and installed boot identity are rechecked before capture.
The held output filesystem must resolve to one unique physical disk distinct
from the source; unknown, stacked, ambiguous or same-source ancestry refuses
before any output directory or allocation. The fixed output parent is
`/srv/vaultsync-recovery/captures`, already provisioned root:root0700 on ext4 by
the trusted media assembly. Runtime capture does not adopt an unknown parent,
create a repository on an arbitrary GUI path, or format any disk.

The source copy uses existing complete-image sealing, isolated inspection,
readback and create-only publication. Existing images are never overwritten.
The capture records the actual compiled version and source commit. Failure,
cancellation and publication uncertainty remain distinct; a partial image is
not a usable completed backup. Terminal statuses and empty-source/cancel states
are translated in all19 maintained catalogs with the selected console culture.

## Durable result and host health

The exact capture assessment remains independent of result-screen I/O and boot
monitor cleanup. A failure after verified publication does not relabel the
stored image as never published or interrupted. It can still produce host exit74;
that is not a claim of healthy admission/cleanup. Result display has its own
10-second deadline, so a cancellation racing the commit does not erase the
publication result or block the service indefinitely. Pre-publication admission
failures remain fail-closed. PublicationUnconfirmed is never promoted to success.

## Image access and remaining integration

Published originals remain root-private0400 files inside0700 directories.
The nonroot graphical restore controller cannot read them just because capture
succeeded. A reviewed export/descriptor boundary is still required for the
complete product-created-image recovery flow. Never solve this by chmod on an
original backup, hardlinking it into a public directory, or opening arbitrary
paths with privilege on behalf of the GUI. Graphical capture selection is also
not delivered by this console component.

The capture directory must be provisioned as part of a real media/storage
installation. A staged service template alone is not bootable recovery media.
Native installed capture/cancel/publication, usable image access, independent
boot/physical authorization and a full OS restore loop remain open before Linux
disk beta qualification. The desktop app remains supported on Windows, macOS
and Linux throughout the staged beta program.

## Verification checkpoint — 2026-11-06

Independent review caught and corrected a production catalog-provider mismatch
and separation of durable capture from presentation/host health.194 focused
warning-as-error Release cases pass, alongside nine producer tests and a
RecoveryHost Release build with zero warnings/errors. The first review test run
had six failures in an output-only console fake; the fixture was corrected
without relaxing production checks, and the failed run remains historical.
Localization covers2,008 keys in19 catalogs. Existing native ext4 transaction
rows do not execute kernel storage on macOS; no native test is inferred from
their ordinary passing test rows. No unrelated full suite was repeated here.
