#!/usr/bin/env python3
"""Generate boot configuration for a staged Linux x64 recovery disk.

This tool writes ordinary build files only. The media assembler installs the
result at /etc/fstab and /boot/grub/grub.cfg in the offline image.
"""

import argparse
from pathlib import Path
import re
from uuid import UUID


def filesystem_uuid(value: str) -> UUID:
    parsed = UUID(value)
    if parsed.int == 0 or str(parsed) != value:
        raise ValueError("expected a canonical, nonzero filesystem UUID")
    return parsed


def boot_config(root: str, scratch: str, export: str, kernel: str) -> tuple[str, str]:
    if len({filesystem_uuid(value) for value in (root, scratch, export)}) != 3:
        raise ValueError("root, scratch and export UUIDs must be distinct")
    if not re.fullmatch(r"[0-9][A-Za-z0-9.+_-]{0,95}", kernel):
        raise ValueError("invalid Linux kernel release")
    fstab = f"""UUID={root} / ext4 ro 0 0
UUID={scratch} /srv/vaultsync-recovery ext4 rw,nosuid,nodev,noexec 0 0
UUID={export} /srv/vaultsync-recovery-exports ext4 rw,nosuid,nodev,noexec 0 0
tmpfs /tmp tmpfs mode=1777,nosuid,nodev 0 0
tmpfs /var/tmp tmpfs mode=1777,nosuid,nodev 0 0
tmpfs /var/log tmpfs mode=0755,nosuid,nodev 0 0
tmpfs /var/lib tmpfs mode=0755,nosuid,nodev 0 0
tmpfs /var/cache tmpfs mode=0755,nosuid,nodev 0 0
tmpfs /var/spool tmpfs mode=0755,nosuid,nodev 0 0
"""
    grub = f"""set default=0
set timeout=2
menuentry 'VaultSync offline recovery' {{
    search --no-floppy --fs-uuid --set=root {root}
    linux /boot/vmlinuz-{kernel} root=UUID={root} ro fsck.mode=skip console=tty0 console=ttyS0,115200n8
    initrd /boot/initrd.img-{kernel}
}}
"""
    return fstab, grub


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root-uuid", required=True)
    parser.add_argument("--scratch-uuid", required=True)
    parser.add_argument("--export-uuid", required=True)
    parser.add_argument("--kernel-release", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        fstab, grub = boot_config(args.root_uuid, args.scratch_uuid, args.export_uuid, args.kernel_release)
        args.output.mkdir(mode=0o700)
        for name, content in (("fstab", fstab), ("grub.cfg", grub)):
            with (args.output / name).open("x", encoding="ascii", newline="\n") as file:
                file.write(content)
    except (OSError, ValueError) as error:
        parser.exit(1, f"recovery boot configuration refused: {error}\n")


if __name__ == "__main__":
    main()
