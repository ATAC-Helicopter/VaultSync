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
restore_uuid=
if [[ -e restore-installation || -L restore-installation ]]; then
    [[ -d restore-installation && ! -L restore-installation ]]
    python3 - <<'PYVALIDATE'
import json
from pathlib import Path

def manifest(folder):
    return json.loads((Path(folder) / "recovery.installation.json").read_text())

capture = manifest("installation")
restore = manifest("restore-installation")
if (capture["controllerMode"], restore["controllerMode"]) != ("capture", "graphical"):
    raise SystemExit("Combined media requires capture and graphical installations, in that order.")
if any(capture[key] != restore[key]
       for key in ("sourceCommit", "runtimeIdentifier", "systemdSha256", "consoleCulture")):
    raise SystemExit("Combined media requires both installations from the same build and locale.")
PYVALIDATE
    restore_uuid=$(uuidgen)
fi
work=$(mktemp -d -- "$PWD/.recovery-media.XXXXXX")
trap 'podman unshare rm -rf -- "$work"' EXIT

scratch_uuid=$2
export_uuid=$3
stage_root() {
    local source=$1 filesystem=$2 name=$3
    shift 3
    local stage="$work/$name"
    mkdir -p -- "$stage/inputs" "$stage/output" "$stage/rootfs"
    python3 "$repo/scripts/recovery_boot_config.py" --root-uuid "$filesystem" \
        --scratch-uuid "$scratch_uuid" --export-uuid "$export_uuid" --kernel-release "$kernel" \
        --output "$stage/inputs/boot-config" "$@"
    cp -a -- "$source" "$stage/inputs/installation"
    cp -- "$repo/packaging/recovery/stage-debian-root.sh" "$stage/inputs/"
    podman run --rm --pull never --network none \
        --volume "$stage/inputs:/inputs:ro,Z" --volume "$stage/output:/output:rw,Z" \
        "$image" /bin/sh /inputs/stage-debian-root.sh
    podman unshare tar --numeric-owner -xf "$stage/output/recovery-root.tar" -C "$stage/rootfs"
    podman unshare mkdir -- "$stage/rootfs/dev" "$stage/rootfs/proc" "$stage/rootfs/run" "$stage/rootfs/sys"
    truncate -s 3G "$work/$name.ext4"
    podman unshare mkfs.ext4 -q -F -d "$stage/rootfs" -U "$filesystem" -L VSRECOVERY "$work/$name.ext4"
    e2fsck -fn "$work/$name.ext4"
}

extra=()
if [[ -n $restore_uuid ]]; then
    stage_root installation "$root_uuid" root --restore-root-uuid "$restore_uuid"
    stage_root restore-installation "$restore_uuid" restore
    read -r restore_hash _ < <(sha256sum "$work/restore.ext4")
    extra=(--restore-guid "$(uuidgen)" --restore-sha256 "$restore_hash")
else
    stage_root installation "$root_uuid" root
fi

(
    cd -- "$work"
    truncate -s 256M esp.fat
    mkfs.vfat -F 32 -n VSBOOT esp.fat
    mmd -i esp.fat ::/EFI ::/EFI/BOOT
    mcopy -i esp.fat root/output/BOOTX64.EFI ::/EFI/BOOT/BOOTX64.EFI
    read -r esp_hash _ < <(sha256sum esp.fat)
    read -r root_hash _ < <(sha256sum root.ext4)
    python3 "$repo/scripts/assemble_recovery_disk.py" \
        --disk-guid "$(uuidgen)" --esp-guid "$(uuidgen)" --root-guid "$(uuidgen)" \
        --esp-sha256 "$esp_hash" --root-sha256 "$root_hash" "${extra[@]}"
    sgdisk --verify recovery.raw
)
ln -T -- "$work/recovery.raw" recovery.raw
sha256sum recovery.raw
