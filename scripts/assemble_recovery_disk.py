#!/usr/bin/env python3
"""Assemble a standalone GPT recovery image from verified EFI and ext4 files.

Run in the private build directory containing esp.fat and root.ext4. The
assembler creates recovery.raw there and never mounts or formats a device.
Run it with the pinned sgdisk build used by the packaging environment.
"""

import argparse
from contextlib import contextmanager
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
    disk_guid, esp_guid, root_guid = (filesystem_uuid(guid) for guid in
                                     (args.disk_guid, args.esp_guid, args.root_guid))
    if len({disk_guid, esp_guid, root_guid}) != 3:
        raise ValueError("GPT identifiers must be distinct")
    if any(not re.fullmatch(r"[0-9a-f]{64}", value) for value in (args.esp_sha256, args.root_sha256)):
        raise ValueError("expected two lowercase SHA-256 digests")
    output_path = Path.cwd() / "recovery.raw"
    if os.path.lexists(output_path):
        raise FileExistsError("output already exists")
    with open_image(Path("esp.fat")) as (esp, esp_size), open_image(Path("root.ext4")) as (root, root_size):
        if esp_size != ESP_BYTES or not 0 < root_size <= MAX_ROOT_BYTES or root_size % SECTOR:
            raise ValueError("expected a 256 MiB EFI image and bounded, sector-aligned ext4 image")
        end = ROOT_START + root_size // SECTOR - 1
        with TemporaryDirectory(prefix=".recovery-disk-", dir=Path.cwd()) as directory:
            image = Path(directory) / "media.raw"
            with image.open("xb") as output:
                output.truncate((end + 1 + BACKUP_GPT_SECTORS) * SECTOR)
                copy_verified(esp, output, ESP_START * SECTOR, esp_size, args.esp_sha256)
                copy_verified(root, output, ROOT_START * SECTOR, root_size, args.root_sha256)
            subprocess.run([
                "sgdisk", "--clear", "--set-alignment=1", f"--disk-guid={disk_guid}",
                f"--new=1:{ESP_START}:{ROOT_START - 1}", "--typecode=1:ef00",
                f"--partition-guid=1:{esp_guid}", "--change-name=1:",
                f"--new=2:{ROOT_START}:{end}", "--typecode=2:8300",
                f"--partition-guid=2:{root_guid}", "--change-name=2:", "media.raw",
            ], cwd=directory, check=True)
            image.chmod(0o444)
            os.link(image, output_path)


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("disk-guid", "esp-guid", "root-guid", "esp-sha256", "root-sha256"):
        parser.add_argument("--" + name, required=True)
    try:
        assemble(parser.parse_args(argv))
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"recovery disk assembly refused: {error}\n")


if __name__ == "__main__":
    main()
