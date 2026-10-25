#!/usr/bin/env python3
"""Stage the read-only Linux x64 worker from a reviewed self-contained build."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import selectors
import time
import shutil
import subprocess
import tempfile

ROLE = "read-only-inspection-v1"
MANIFEST = "worker.installation.json"
REQUIRED = {"VaultSync.DiskWorker", "VaultSync.DiskWorker.dll", "VaultSync.DiskWorker.deps.json",
            "VaultSync.DiskWorker.runtimeconfig.json", "VaultSync.Core.dll", "libhostfxr.so",
            "libhostpolicy.so", "libcoreclr.so", "System.Private.CoreLib.dll"}
MAX_FILE = 64 * 1024 * 1024
MAX_TOTAL = 512 * 1024 * 1024


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def strict_json(data):
    def pairs(values):
        result = {}
        for key, value in values:
            if key in result:
                raise ValueError("duplicate JSON property")
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=pairs)


def read_identity(executable, directory, timeout=10):
    if os.name != "posix":
        raise ValueError("worker identity requires POSIX pipes")
    process = subprocess.Popen([str(executable), "--installation-identity"], cwd=directory,
                               env={"LANG": "C", "DOTNET_EnableDiagnostics": "0",
                                    "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT": "1"},
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    data = bytearray()
    deadline = time.monotonic() + timeout
    try:
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ, "output")
            selector.register(process.stderr, selectors.EVENT_READ, "error")
            while selector.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise ValueError("worker identity deadline")
                events = selector.select(remaining)
                if not events:
                    raise ValueError("worker identity deadline")
                for key, _ in events:
                    chunk = os.read(key.fileobj.fileno(), 513 - len(data) if key.data == "output" else 1)
                    if not chunk:
                        selector.unregister(key.fileobj)
                    elif key.data == "error":
                        raise ValueError("worker identity contains diagnostics")
                    else:
                        data.extend(chunk)
                        if len(data) > 512:
                            raise ValueError("worker identity exceeds bound")
        if process.wait(timeout=max(0.001, deadline - time.monotonic())) != 0 or not data:
            raise ValueError("worker build identity unavailable")
        return strict_json(data)
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=2)
        process.stdout.close()
        process.stderr.close()


def stage(worker_dir, app_dir, commit):
    worker_dir, app_dir = Path(worker_dir), Path(app_dir)
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("full source commit required")
    if not worker_dir.is_dir() or worker_dir.is_symlink() or not app_dir.is_dir() or app_dir.is_symlink():
        raise ValueError("existing regular build directories required")
    destination = app_dir / "DiskWorker"
    if destination.exists() or destination.is_symlink():
        raise ValueError("worker destination already exists")
    records = []
    total = 0
    for entry in sorted(worker_dir.iterdir(), key=lambda path: path.name):
        if (len(records) >= 512 or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", entry.name)
                or entry.name == MANIFEST or entry.is_symlink() or not entry.is_file()):
            raise ValueError("worker closure must contain only bounded regular leaf files")
        status = entry.stat()
        if status.st_nlink != 1 or not 0 < status.st_size <= MAX_FILE:
            raise ValueError("worker file identity or length refused")
        total += status.st_size
        if total > MAX_TOTAL:
            raise ValueError("worker closure exceeds bounds")
        records.append({"name": entry.name, "size": status.st_size, "sha256": digest(entry),
                        "executable": bool(status.st_mode & 0o111)})
    if not REQUIRED.issubset({entry["name"] for entry in records}):
        raise ValueError("incomplete self-contained worker")
    if not next(record for record in records if record["name"] == "VaultSync.DiskWorker")["executable"]:
        raise ValueError("worker apphost must be executable")
    app_core = app_dir / "VaultSync.Core.dll"
    if app_core.is_symlink() or not app_core.is_file() or digest(app_core) != digest(worker_dir / "VaultSync.Core.dll"):
        raise ValueError("worker and application Core builds differ")
    config = strict_json((worker_dir / "VaultSync.DiskWorker.runtimeconfig.json").read_bytes())["runtimeOptions"]
    if ("framework" in config or "frameworks" in config or "additionalProbingPaths" in config
            or not config.get("includedFrameworks")):
        raise ValueError("fixed self-contained runtime required")
    dependencies = strict_json((worker_dir / "VaultSync.DiskWorker.deps.json").read_bytes())
    if not dependencies.get("runtimeTarget", {}).get("name", "").endswith("/linux-x64"):
        raise ValueError("worker runtime target differs")
    # Execute only the just-built fixed identity mode, without inherited hooks,
    # credentials or home settings. This is a build operation, never elevation.
    observed = read_identity((worker_dir / "VaultSync.DiskWorker").resolve(), worker_dir)
    expected = {"schemaVersion": 1, "role": ROLE, "runtimeIdentifier": "linux-x64", "sourceCommit": commit}
    if observed != expected or type(observed.get("schemaVersion")) is not int:
        raise ValueError("worker source, runtime or role differs")
    value = {**expected, "files": records}
    encoded = (json.dumps(value, indent=2, ensure_ascii=True) + "\n").encode()
    if len(encoded) > 128 * 1024:
        raise ValueError("worker manifest exceeds bound")
    temporary = Path(tempfile.mkdtemp(prefix=".worker-build-", dir=app_dir))
    try:
        temporary.chmod(0o755)
        for record in records:
            target = temporary / record["name"]
            shutil.copyfile(worker_dir / record["name"], target)
            target.chmod(0o755 if record["executable"] else 0o644)
            if target.stat().st_size != record["size"] or digest(target) != record["sha256"]:
                raise ValueError("worker changed during staging")
        (temporary / MANIFEST).write_bytes(encoded)
        (temporary / MANIFEST).chmod(0o644)
        if digest(app_core) != next(record["sha256"] for record in records if record["name"] == "VaultSync.Core.dll"):
            raise ValueError("application Core changed during staging")
        if destination.exists() or destination.is_symlink():
            raise ValueError("worker destination changed")
        temporary.rename(destination)
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)
    return len(records)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker-dir", type=Path, required=True)
    parser.add_argument("--app-dir", type=Path, required=True)
    parser.add_argument("--source-commit", required=True)
    args = parser.parse_args()
    try:
        count = stage(args.worker_dir, args.app_dir, args.source_commit)
    except (ValueError, OSError, KeyError, TypeError, subprocess.SubprocessError) as error:
        parser.exit(1, "Worker staging refused: " + str(error) + "\n")
    print("Read-only Linux x64 worker staged:", count, "bound files; no privilege granted.")


if __name__ == "__main__":
    main()
