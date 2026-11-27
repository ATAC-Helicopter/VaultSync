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


def boot_config(root: str, scratch: str, export: str, kernel: str,
                restore_root: str | None = None) -> tuple[str, str]:
    volumes = (root, scratch, export) if restore_root is None else (root, scratch, export, restore_root)
    if len({filesystem_uuid(value) for value in volumes}) != len(volumes):
        raise ValueError("recovery filesystem UUIDs must be distinct")
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
    def entry(label: str, filesystem: str) -> str:
        return f"""menuentry '{label}' {{
    search --no-floppy --fs-uuid --set=root {filesystem}
    linux /boot/vmlinuz-{kernel} root=UUID={filesystem} ro fsck.mode=skip console=tty0 console=ttyS0,115200n8
    initrd /boot/initrd.img-{kernel}
}}
"""
    if restore_root is None:
        grub = "set default=0\nset timeout=2\n" + entry("VaultSync offline recovery", root)
    else:
        grub = ("set default=0\nset timeout=-1\n"
                + entry("VaultSync - Create disk image", root)
                + entry("VaultSync - Restore disk image", restore_root))
    return fstab, grub


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root-uuid", required=True)
    parser.add_argument("--scratch-uuid", required=True)
    parser.add_argument("--export-uuid", required=True)
    parser.add_argument("--kernel-release", required=True)
    parser.add_argument("--restore-root-uuid")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        fstab, grub = boot_config(args.root_uuid, args.scratch_uuid, args.export_uuid,
                                 args.kernel_release, args.restore_root_uuid)
        args.output.mkdir(mode=0o700)
        for name, content in (("fstab", fstab), ("grub.cfg", grub)):
            with (args.output / name).open("x", encoding="ascii", newline="\n") as file:
                file.write(content)
    except (OSError, ValueError) as error:
        parser.exit(1, f"recovery boot configuration refused: {error}\n")


if __name__ == "__main__":
    main()
