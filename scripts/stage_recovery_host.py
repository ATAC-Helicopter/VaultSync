#!/usr/bin/env python3
"""Create a new, source-bound headless Linux recovery installation (not signing).

Inputs: --host-dir/--controller-dir self-contained linux-x64 publishes; --reader-dir
is the UNCHANGED, already staged DiskWorker closure including worker.installation.json.
--launcher is the fixed reviewed native controller-launch executable. --systemd is
a regular copy of the target media's /usr/lib/systemd/systemd, within build roots.
--output must not exist. --console-culture en/it is explicit packaging intent,
not a physical-font or recovery-profile qualification.

Compile launcher on Linux with argv (not a shell-expanded source/path):
cc -std=c11 -O2 -Wall -Wextra -Werror -fstack-protector-strong -D_FORTIFY_SOURCE=3
   '-DSOURCE_COMMIT="<40 lowercase hex>"' controller-launch.c
   -Wl,-z,relro,-z,now -o recovery-controller-launch

Install resulting tree at /usr/lib/vaultsync-recovery, its boot unit separately
at /usr/lib/systemd/system/vaultsync-recovery.service; all root-owned and on real
read-only media/root mounts. Global /run and /tmp must be writable tmpfs mounts;
/srv/vaultsync-recovery must be separate private root0700 writable ext4. The unit
shares PID1 namespaces and requires global nonloopback interfaces down. There is
no writable-cloud, marker, desktop elevation or publisher-signing substitute.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import shutil
import stat
import subprocess

_spec = importlib.util.spec_from_file_location("_recovery_worker_stager", Path(__file__).with_name("stage_disk_worker.py"))
worker = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(worker)
MAX_FILE = 64 * 1024 * 1024
MAX_TOTAL = 1024 * 1024 * 1024
MAX_FILES = 2048
MANIFEST = "recovery.installation.json"
REPOSITORY = Path(__file__).resolve().parents[1]


def checked_directory(path):
    original = Path(path).absolute()
    result = worker.build_directory(original)
    if original != result:
        raise ValueError("aliased build input")
    return result


def checked_file(path):
    value = Path(path).absolute()
    parent = checked_directory(value.parent)
    value = parent / value.name
    info = value.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or not 0 < info.st_size <= MAX_FILE or info.st_mode & 0o6022:
        raise ValueError("unsafe build file")
    return value


def flat(directory):
    records = []
    for value in sorted(directory.iterdir()):
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", value.name):
            raise ValueError("unsafe closure name")
        records.append(checked_file(value))
        if len(records) > 512:
            raise ValueError("closure count")
    return records


def identity(program, role, commit):
    value = worker.read_identity(program, program.parent)
    expected = {"schemaVersion": 1, "role": role, "runtimeIdentifier": "linux-x64", "sourceCommit": commit}
    if value != expected or type(value.get("schemaVersion")) is not int:
        raise ValueError("build role or source differs")


def runtime(directory, name):
    required = {name, name + ".dll", name + ".deps.json", name + ".runtimeconfig.json", "VaultSync.Core.dll",
                "libhostfxr.so", "libhostpolicy.so", "libcoreclr.so", "System.Private.CoreLib.dll"}
    if not required.issubset({file.name for file in directory.iterdir()}):
        raise ValueError("incomplete runtime")
    if not (directory / name).stat().st_mode & 0o111:
        raise ValueError("nonexecutable apphost")
    config = worker.strict_json((directory / (name + ".runtimeconfig.json")).read_bytes())["runtimeOptions"]
    if any(key in config for key in ("framework", "frameworks", "additionalProbingPaths")) or not config.get("includedFrameworks"):
        raise ValueError("self-contained runtime required")
    deps = worker.strict_json((directory / (name + ".deps.json")).read_bytes())
    if not deps.get("runtimeTarget", {}).get("name", "").endswith("/linux-x64"):
        raise ValueError("wrong runtime identifier")


def reader_manifest(directory, commit):
    encoded = (directory / "worker.installation.json").read_bytes()
    if len(encoded) > 128 * 1024:
        raise ValueError("reader manifest bound")
    value = worker.strict_json(encoded)
    if set(value) != {"schemaVersion", "role", "runtimeIdentifier", "sourceCommit", "files"} or \
            type(value.get("schemaVersion")) is not int or value["schemaVersion"] != 1 or \
            value["role"] != "read-only-inspection-v1" or value["runtimeIdentifier"] != "linux-x64" or value["sourceCommit"] != commit:
        raise ValueError("reader role differs")
    records = value["files"]
    observed = []
    for file in flat(directory):
        if file.name == "worker.installation.json":
            continue
        observed.append({"name": file.name, "size": file.stat().st_size, "sha256": worker.digest(file),
                         "executable": bool(file.stat().st_mode & 0o111)})
    if records != observed or not worker.REQUIRED.issubset({record["name"] for record in records}):
        raise ValueError("reader closure changed")


def controller_files(directory):
    records = []
    for entry in sorted(directory.iterdir()):
        if entry.name == "Localization" and entry.is_dir() and not entry.is_symlink():
            expected = {file.name for file in (REPOSITORY / "Localization").glob("strings.*.json")}
            if {file.name for file in entry.iterdir()} != expected or len(expected) != 19:
                raise ValueError("controller catalogs incomplete")
            records.extend(("Localization/" + file.name, checked_file(file)) for file in sorted(entry.iterdir()))
        else:
            if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", entry.name):
                raise ValueError("unsafe controller closure name")
            records.append((entry.name, checked_file(entry)))
    if len(records) > 600:
        raise ValueError("controller closure bound")
    return records


def stage(host_dir, controller_dir, reader_dir, launcher, systemd, output, commit, culture,
          controller_mode="headless", display_launcher=None, xorg=None):
    if not re.fullmatch(r"[0-9a-f]{40}", commit) or culture not in ("en", "it"):
        raise ValueError("source or console culture")
    host, controller, reader = map(checked_directory, (host_dir, controller_dir, reader_dir))
    launcher, systemd = checked_file(launcher), checked_file(systemd)
    output = Path(output).absolute()
    parent = checked_directory(output.parent)
    if parent / output.name != output or output.name.startswith(".") or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", output.name):
        raise ValueError("output alias")
    if output.exists() or output.is_symlink():
        raise ValueError("output exists")
    sources = []
    for prefix, directory in (("host", host), ("controller", controller), ("host/DiskWorker", reader)):
        files = controller_files(directory) if prefix == "controller" else [(file.name, file) for file in flat(directory)]
        sources.extend((prefix + "/" + name, file) for name, file in files)
    if launcher.name != "recovery-controller-launch" or not launcher.stat().st_mode & 0o111:
        raise ValueError("fixed launcher required")
    sources.append(("launch/recovery-controller-launch", launcher))
    if controller_mode not in ("headless", "graphical"):
        raise ValueError("controller mode")
    graphical = controller_mode == "graphical"
    if graphical:
        if display_launcher is None or xorg is None:
            raise ValueError("graphical dependencies missing")
        display_launcher, xorg = checked_file(display_launcher), checked_file(xorg)
        if display_launcher.name != "recovery-display-launch" or not display_launcher.stat().st_mode & 0o111 or not xorg.stat().st_mode & 0o111:
            raise ValueError("graphical executables invalid")
        if not (controller / "Localization").is_dir():
            raise ValueError("graphical catalogs missing")
        sources.append(("launch/recovery-display-launch", display_launcher))
        sources.append(("boot/vaultsync-recovery-display.service", checked_file(REPOSITORY / "packaging/recovery/vaultsync-recovery-display.service")))
    elif display_launcher is not None or xorg is not None:
        raise ValueError("headless display dependencies")
    unit = checked_file(REPOSITORY / ("packaging/recovery/vaultsync-recovery-graphical.service" if graphical else "packaging/recovery/vaultsync-recovery.service"))
    sources.append(("boot/vaultsync-recovery.service", unit))
    catalogs = checked_directory(REPOSITORY / "Localization")
    catalog_files = sorted(catalogs.glob("strings.*.json"))
    if len(catalog_files) != 19:
        raise ValueError("maintained catalogs missing")
    for file in catalog_files:
        file = checked_file(file)
        parsed = worker.strict_json(file.read_bytes())
        for key in ("Title", "Warning", "Target", "Capacity", "Image", "Manifest", "Hardware", "Challenge", "Deadline"):
            if not isinstance(parsed.get("Recovery.Console." + key), str) or not parsed["Recovery.Console." + key]:
                raise ValueError("console translation missing")
        sources.append(("catalogs/" + file.name, file))
    runtime(host, "VaultSync.RecoveryHost"); runtime(controller, "VaultSync.RecoveryController")
    reader_manifest(reader, commit)
    cores = [worker.digest(directory / "VaultSync.Core.dll") for directory in (host, controller, reader)]
    if len(set(cores)) != 1:
        raise ValueError("Core builds differ")
    records = [{"path": relative, "size": file.stat().st_size, "sha256": worker.digest(file),
                "executable": bool(file.stat().st_mode & 0o111)} for relative, file in sorted(sources)]
    if len(records) > MAX_FILES or len({record["path"] for record in records}) != len(records) or sum(record["size"] for record in records) > MAX_TOTAL:
        raise ValueError("media closure bounds")
    # All path/input/size checks precede executable metadata queries or mutation.
    identity(host / "VaultSync.RecoveryHost", "offline-recovery-supervisor-v1", commit)
    identity(controller / "VaultSync.RecoveryController", "recovery-controller-v1", commit)
    identity(launcher, "recovery-controller-launch-v1", commit)
    if graphical:
        identity(display_launcher, "recovery-display-launch-v1", commit)
    value = {"schemaVersion": 2, "role": "offline-recovery-media-v1", "runtimeIdentifier": "linux-x64",
             "sourceCommit": commit, "consoleCulture": culture, "systemdSha256": worker.digest(systemd), "files": records,
             "controllerMode": controller_mode, "xorgSha256": worker.digest(xorg) if graphical else None}
    encoded = (json.dumps(value, indent=2, ensure_ascii=True) + "\n").encode()
    if len(encoded) > 1024 * 1024:
        raise ValueError("media manifest bound")
    # Exclusive creation, then manifest LAST. There is no rename-over-empty race.
    # Incomplete trees cannot pass runtime admission and are removed only if still
    # the exact build-owned inode created here.
    output.mkdir(mode=0o755)
    output.chmod(0o755)
    owned = output.stat()
    try:
        for record, (_, source) in zip(records, sorted(sources)):
            target = output / record["path"]
            target.parent.mkdir(parents=True, exist_ok=True)
            target.parent.chmod(0o755)
            with source.open("rb") as input_file, target.open("xb") as destination:
                shutil.copyfileobj(input_file, destination, 1024 * 1024)
            target.chmod(0o755 if record["executable"] else 0o644)
            if target.stat().st_size != record["size"] or worker.digest(target) != record["sha256"]:
                raise ValueError("source changed during staging")
        with (output / MANIFEST).open("xb") as manifest:
            manifest.write(encoded)
        (output / MANIFEST).chmod(0o644)
        return len(records)
    except BaseException:
        current = output.lstat()
        if stat.S_ISDIR(current.st_mode) and (current.st_dev, current.st_ino) == (owned.st_dev, owned.st_ino) and output.resolve() == output:
            shutil.rmtree(output)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("host-dir", "controller-dir", "reader-dir", "launcher", "systemd", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--console-culture", choices=("en", "it"), required=True)
    parser.add_argument("--controller-mode", choices=("headless", "graphical"), default="headless")
    parser.add_argument("--display-launcher", type=Path)
    parser.add_argument("--xorg", type=Path)
    args = parser.parse_args()
    try:
        count = stage(args.host_dir, args.controller_dir, args.reader_dir, args.launcher, args.systemd, args.output,
                      args.source_commit, args.console_culture, args.controller_mode, args.display_launcher, args.xorg)
    except (ValueError, OSError, KeyError, TypeError, subprocess.SubprocessError):
        parser.exit(1, "recovery-stage.refused\n")
    print("recovery-stage.completed", count)


if __name__ == "__main__":
    main()
