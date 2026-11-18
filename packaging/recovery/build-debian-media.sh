#!/usr/bin/env bash
# Assemble a personalized offline medium from a trusted staged installation.
set -euo pipefail
umask 077

if [[ $# -ne 3 || ! $1 =~ ^[0-9a-f]{64}$ ]]; then
    echo "Usage: bash build-debian-media.sh <local Debian image ID> <scratch UUID> <export UUID>" >&2
    exit 2
fi
if [[ $(id -u) == 0 || $(podman info --format '{{.Host.Security.Rootless}}') != true ]]; then
    echo "Run the media builder with rootless Podman." >&2
    exit 1
fi
if [[ ! -d installation || -L installation || -e recovery.raw || -L recovery.raw ]]; then
    echo "Use a private workspace containing installation/ and no recovery.raw." >&2
    exit 1
fi

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
image=$1
kernel=$(podman run --rm --pull never --network none "$image" /bin/sh -ec '
    set -- /boot/vmlinuz-*
    test "$#" -eq 1
    test -f "$1"
    printf "%s\n" "${1#/boot/vmlinuz-}"
')
root_uuid=$(uuidgen)
work=$(mktemp -d -- "$PWD/.recovery-media.XXXXXX")
trap 'podman unshare rm -rf -- "$work"' EXIT

mkdir -- "$work/inputs" "$work/output" "$work/rootfs"
python3 "$repo/scripts/recovery_boot_config.py" --root-uuid "$root_uuid" \
    --scratch-uuid "$2" --export-uuid "$3" --kernel-release "$kernel" \
    --output "$work/inputs/boot-config"
cp -a -- installation "$work/inputs/"
cp -- "$repo/packaging/recovery/stage-debian-root.sh" "$work/inputs/"
podman run --rm --pull never --network none \
    --volume "$work/inputs:/inputs:ro,Z" --volume "$work/output:/output:rw,Z" \
    "$image" /bin/sh /inputs/stage-debian-root.sh

(
    cd -- "$work"
    podman unshare tar --numeric-owner -xf output/recovery-root.tar -C rootfs
    podman unshare mkdir -- rootfs/dev rootfs/proc rootfs/run rootfs/sys
    truncate -s 3G root.ext4
    podman unshare mkfs.ext4 -q -F -d rootfs -U "$root_uuid" -L VSRECOVERY root.ext4
    e2fsck -fn root.ext4

    truncate -s 256M esp.fat
    mkfs.vfat -F 32 -n VSBOOT esp.fat
    mmd -i esp.fat ::/EFI ::/EFI/BOOT
    mcopy -i esp.fat output/BOOTX64.EFI ::/EFI/BOOT/BOOTX64.EFI
    read -r esp_hash _ < <(sha256sum esp.fat)
    read -r root_hash _ < <(sha256sum root.ext4)
    python3 "$repo/scripts/assemble_recovery_disk.py" \
        --disk-guid "$(uuidgen)" --esp-guid "$(uuidgen)" --root-guid "$(uuidgen)" \
        --esp-sha256 "$esp_hash" --root-sha256 "$root_hash"
    sgdisk --verify recovery.raw
)
ln -T -- "$work/recovery.raw" recovery.raw
sha256sum recovery.raw
