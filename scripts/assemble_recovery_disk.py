#!/usr/bin/env python3
"""Assemble a standalone GPT recovery image from verified EFI and ext4 files.

Run in the private build directory containing esp.fat and root.ext4. The
assembler creates recovery.raw there and never mounts or formats a device.
Run it with the pinned sgdisk build used by the packaging environment.
"""

import argparse
from contextlib import ExitStack, contextmanager
import hashlib
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
from tempfile import TemporaryDirectory

from recovery_boot_config import filesystem_uuid

SECTOR = 512
ESP_START = 2048
ESP_BYTES = 256 * 1024**2
ROOT_START = ESP_START + ESP_BYTES // SECTOR
BACKUP_GPT_SECTORS = 33
MAX_ROOT_BYTES = 16 * 1024**3


@contextmanager
def open_image(path: Path):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "rb") as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode):
            raise ValueError("filesystem images must be regular files")
        yield source, info.st_size


def copy_verified(source, target, offset: int, size: int, expected_hash: str) -> None:
    target.seek(offset)
    digest = hashlib.sha256()
    remaining = size
    zero = bytes(1024**2)
    while remaining:
        data = source.read(min(remaining, len(zero)))
        if not data:
            raise ValueError("filesystem image was truncated")
        remaining -= len(data)
        digest.update(data)
        if data == zero[:len(data)]:
            target.seek(len(data), os.SEEK_CUR)
        else:
            target.write(data)
    if source.read(1) or digest.hexdigest() != expected_hash:
        raise ValueError("filesystem image does not match its expected SHA-256")


def assemble(args) -> None:
    if sys.platform != "linux":
        raise ValueError("the disk assembler requires Linux")
    partitions = [
        (Path("esp.fat"), "ef00", filesystem_uuid(args.esp_guid), args.esp_sha256),
        (Path("root.ext4"), "8300", filesystem_uuid(args.root_guid), args.root_sha256),
    ]
    if args.restore_guid is not None or args.restore_sha256 is not None:
        if args.restore_guid is None or args.restore_sha256 is None:
            raise ValueError("the restore partition needs both a GUID and a SHA-256")
        partitions.append((Path("restore.ext4"), "8300",
                           filesystem_uuid(args.restore_guid), args.restore_sha256))
    disk_guid = filesystem_uuid(args.disk_guid)
    identifiers = [disk_guid, *(part[2] for part in partitions)]
    if len(set(identifiers)) != len(identifiers):
        raise ValueError("GPT identifiers must be distinct")
    if any(not re.fullmatch(r"[0-9a-f]{64}", part[3]) for part in partitions):
        raise ValueError("expected lowercase SHA-256 digests")
    output_path = Path.cwd() / "recovery.raw"
    if os.path.lexists(output_path):
        raise FileExistsError("output already exists")
    with ExitStack() as stack:
        start = ESP_START
        inputs = []
        command = ["sgdisk", "--clear", "--set-alignment=1", f"--disk-guid={disk_guid}"]
        for index, (path, kind, guid, digest) in enumerate(partitions, start=1):
            source, size = stack.enter_context(open_image(path))
            if (index == 1 and size != ESP_BYTES) or not 0 < size <= MAX_ROOT_BYTES or size % SECTOR:
                raise ValueError("expected a 256 MiB EFI image and bounded, sector-aligned ext4 images")
            end = start + size // SECTOR - 1
            command.extend([f"--new={index}:{start}:{end}", f"--typecode={index}:{kind}",
                            f"--partition-guid={index}:{guid}", f"--change-name={index}:"])
            inputs.append((source, start * SECTOR, size, digest))
            start = end + 1
        with TemporaryDirectory(prefix=".recovery-disk-", dir=Path.cwd()) as directory:
            image = Path(directory) / "media.raw"
            with image.open("xb") as output:
                output.truncate((start + BACKUP_GPT_SECTORS) * SECTOR)
                for source, offset, size, digest in inputs:
                    copy_verified(source, output, offset, size, digest)
            subprocess.run([*command, "media.raw"], cwd=directory, check=True)
            image.chmod(0o444)
            os.link(image, output_path)


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("disk-guid", "esp-guid", "root-guid", "esp-sha256", "root-sha256"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--restore-guid")
    parser.add_argument("--restore-sha256")
    try:
        assemble(parser.parse_args(argv))
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"recovery disk assembly refused: {error}\n")


if __name__ == "__main__":
    main()
