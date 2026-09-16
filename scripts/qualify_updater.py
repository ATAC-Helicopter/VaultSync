"""Exercise released patch helpers and candidate installers on disposable CI hosts.

This is an executable integration test, not a replacement for interactive
UAC/polkit qualification. It must never install packages on a developer host.
"""
import argparse
import contextlib
import hashlib
import json
import os
import platform
import shutil
import signal
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
from pathlib import Path


REPOSITORY = "ATAC-Helicopter/VaultSync"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def verify_payload(root, manifest):
    root = root.resolve()
    for entry in manifest["files"]:
        path = (root / entry["path"]).resolve()
        if not path.is_relative_to(root):
            raise ValueError("Payload path escapes installation")
        if path.stat().st_size != entry["size"] or sha256(path) != entry["sha256"].lower():
            raise ValueError(f"Installed payload mismatch: {entry['path']}")


def snapshot(root):
    return {str(path.relative_to(root)): sha256(path)
            for path in root.rglob("*") if path.is_file()}


def download_base(version, name, destination):
    headers = {"User-Agent": "VaultSync-updater-qualification"}
    api_headers = dict(headers)
    if os.environ.get("GH_TOKEN"):
        api_headers["Authorization"] = "Bearer " + os.environ["GH_TOKEN"]
    request = urllib.request.Request(
        f"https://api.github.com/repos/{REPOSITORY}/releases/tags/v{version}",
        headers=api_headers)
    with urllib.request.urlopen(request, timeout=60) as response:
        release = json.load(response)
    asset = next(asset for asset in release["assets"] if asset["name"] == name)
    expected_url = f"https://github.com/{REPOSITORY}/releases/download/v{version}/{name}"
    if asset["browser_download_url"] != expected_url:
        raise ValueError("Unexpected base asset URL")
    digest = asset.get("digest", "")
    if not digest.startswith("sha256:") or len(digest) != 71:
        raise ValueError("Released base has no trusted GitHub SHA-256")
    request = urllib.request.Request(expected_url, headers=headers)
    with urllib.request.urlopen(request, timeout=120) as response, destination.open("wb") as output:
        shutil.copyfileobj(response, output)
    if destination.stat().st_size != asset["size"] or sha256(destination) != digest[7:]:
        raise ValueError("Released base failed integrity verification")


def install_windows(installer, destination, log):
    result = subprocess.run(
        [str(installer), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
         f"/DIR={destination}", f"/LOG={log}"], timeout=180)
    if result.returncode != 0:
        raise RuntimeError(f"Windows installer failed: {result.returncode}")


def prepare_base(root, version, system, suffix):
    install = root / "base-install"
    install.mkdir()
    if system == "Windows":
        package = root / "base-setup.exe"
        download_base(version, f"VaultSync-Setup-{version}.exe", package)
        install_windows(package, install, root / "base-install.log")
    elif system == "Linux":
        package = root / "base.tar.gz"
        download_base(version, f"VaultSync-{version}-{suffix}.tar.gz", package)
        with tarfile.open(package) as archive:
            archive.extractall(install, filter="data")
    else:
        package = root / "base.dmg"
        mount = root / "dmg-mount"
        download_base(version, f"VaultSync-{version}-{suffix}.dmg", package)
        subprocess.run(["hdiutil", "attach", str(package), "-readonly", "-nobrowse",
                        "-mountpoint", str(mount)], check=True, timeout=120)
        try:
            shutil.copytree(mount / "VaultSync.app", install / "VaultSync.app", symlinks=True)
        finally:
            subprocess.run(["hdiutil", "detach", str(mount)], check=True, timeout=60)
        install = install / "VaultSync.app"
    return install


def executable(root, system):
    if system == "Darwin":
        return root / "Contents/MacOS/VaultSync.UI"
    return root / ("VaultSync.UI.exe" if system == "Windows" else "VaultSync.UI")


def apply(helper, archive, manifest, install, env, wait_pid=None):
    args = [str(helper), "--apply-patch", str(archive), str(manifest), str(install),
            "--headless-patch"]
    if wait_pid is not None:
        args.append(f"--waitpid={wait_pid}")
    log_path = env.get("VAULTSYNC_QUALIFICATION_LOG")
    if log_path:
        with Path(log_path).open("ab") as output:
            return subprocess.Popen(args, env=env, stdout=output, stderr=output)
    return subprocess.Popen(args, env=env)


@contextlib.contextmanager
def qualification_workspace(evidence):
    with tempfile.TemporaryDirectory(prefix="vaultsync-updater-", dir=os.environ["RUNNER_TEMP"]) as temporary:
        root = Path(temporary)
        try:
            yield root
        finally:
            logs = list(root.rglob("patch-helper.log"))
            if os.environ.get("LOCALAPPDATA"):
                logs.append(Path(os.environ["LOCALAPPDATA"]) / "VaultSync/patch-runtime/patch-helper.log")
            logs.append(Path.home() / "Library/Application Support/VaultSync/patch-runtime/patch-helper.log")
            for index, log in enumerate(logs):
                if log.is_file():
                    shutil.copyfile(log, evidence / f"patch-helper-{index}.log")


def qualify(args):
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("Executable qualification requires a disposable GitHub Actions host")
    system = platform.system()
    suffix = {"Windows": "windows", "Linux":
              "linux-arm64" if platform.machine() in ("aarch64", "arm64") else "linux-x64", "Darwin":
              "macos-apple-silicon" if platform.machine() == "arm64" else "macos-intel"}[system]
    assets = args.assets.resolve()
    manifest_path = next(assets.rglob(f"vaultsync-patch-{suffix}.json"))
    archive = next(assets.rglob(f"vaultsync-patch-{suffix}.zip"))
    manifest = json.loads(manifest_path.read_text())
    if manifest["targetVersion"] != args.target or args.previous not in manifest["baseVersions"]:
        raise ValueError("Candidate patch identity or primary predecessor mismatch")
    if archive.stat().st_size != manifest["archiveSize"] or sha256(archive) != manifest["archiveSha256"].lower():
        raise ValueError("Candidate patch archive failed integrity verification")
    args.evidence.mkdir(parents=True, exist_ok=True)
    evidence = {"platform": system, "previous": args.previous, "target": args.target,
                "archiveSha256": sha256(archive), "checks": []}
    with qualification_workspace(args.evidence) as root:
        base = prepare_base(root, args.previous, system, suffix)
        helper_root = root / "helper"
        shutil.copytree(base, helper_root, symlinks=True)
        install = root / "patch-install"
        shutil.copytree(base, install, symlinks=True)
        if system == "Darwin":
            renamed = root / "PatchInstall.app"
            install.rename(renamed)
            install = renamed
        helper = executable(helper_root, system)
        env = dict(os.environ, VAULTSYNC_CONFIG_DIR=str(root / "profile"),
                   XDG_CONFIG_HOME=str(root / "xdg-config"), XDG_DATA_HOME=str(root / "xdg-data"),
                   VAULTSYNC_QUALIFICATION_LOG=str(args.evidence.resolve() / "helper-output.log"))
        (root / "xdg-config").mkdir()
        (root / "xdg-data").mkdir()
        profile = root / "profile"
        profile.mkdir()
        sentinel = profile / "qualification-sentinel.txt"
        sentinel.write_text("user data must survive")
        before = snapshot(install)
        corrupt = root / "corrupt.zip"
        shutil.copyfile(archive, corrupt)
        with corrupt.open("r+b") as stream:
            stream.write(b"corrupted")
        rejected = apply(helper, corrupt, manifest_path, install, env)
        if rejected.wait(timeout=120) == 0 or snapshot(install) != before:
            raise RuntimeError("Corrupt patch was accepted or changed installed files")
        evidence["checks"].append("corrupt archive rejected without mutation")
        invalid = root / "wrong-base.json"
        invalid_manifest = dict(manifest, previousVersion="0.0.0", baseVersions=["0.0.0"])
        invalid.write_text(json.dumps(invalid_manifest))
        rejected = apply(helper, archive, invalid, install, env)
        if rejected.wait(timeout=120) == 0 or snapshot(install) != before:
            raise RuntimeError("Unlisted base was accepted or changed installed files")
        evidence["checks"].append("unlisted base rejected without mutation")
        parent = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
        process = None
        try:
            process = apply(helper, archive, manifest_path, install, env, parent.pid)
            time.sleep(2)
            if process.poll() is not None or snapshot(install) != before:
                raise RuntimeError(f"Helper did not wait for parent shutdown (exit={process.poll()}); see helper logs")
            parent.terminate()
            parent.wait(timeout=10)
            if process.wait(timeout=180) != 0:
                raise RuntimeError("Released helper failed candidate patch application")
        finally:
            for owned in (parent, process):
                if owned is not None and owned.poll() is None:
                    owned.terminate()
                    owned.wait(timeout=30)
        verify_payload(install, manifest)
        evidence["checks"].append("released helper waited for parent and installed every verified file")
        command = [str(executable(install, system))]
        if system == "Linux":
            command = ["xvfb-run", "-a"] + command
        launched = subprocess.Popen(command, env=env, start_new_session=system != "Windows")
        try:
            time.sleep(8)
            if launched.poll() is not None:
                raise RuntimeError("Updated application exited during startup smoke test")
            evidence["checks"].append("updated application started and remained running")
        finally:
            if launched.poll() is None:
                if system == "Windows":
                    launched.terminate()
                else:
                    os.killpg(launched.pid, signal.SIGTERM)
                launched.wait(timeout=30)
        if sentinel.read_text() != "user data must survive":
            raise RuntimeError("Update changed user data sentinel")
        evidence["checks"].append("external user data preserved")
        if system == "Windows":
            candidate = next(assets.rglob(f"VaultSync-Setup-{args.target}.exe"))
            install_windows(candidate, base, args.evidence / "candidate-install.log")
            verify_payload(base, manifest)
            evidence["checks"].append("candidate installer upgraded released Windows installation")
        elif system == "Linux":
            base_deb = root / "base.deb"
            download_base(args.previous, f"VaultSync-{args.previous}-{suffix}.deb", base_deb)
            candidate = next(assets.rglob(f"VaultSync-{args.target}-{suffix}.deb"))
            for package in (base_deb, candidate):
                subprocess.run(["sudo", "-n", "apt-get", "install", "--reinstall", "-y", str(package)],
                               check=True, timeout=180)
            verify_payload(Path("/opt/vaultsync"), manifest)
            evidence["checks"].append("candidate Debian package upgraded released Linux installation")
        for log in root.rglob("patch-helper.log"):
            shutil.copyfile(log, args.evidence / "patch-helper.log")
    (args.evidence / "updater-qualification.json").write_text(json.dumps(evidence, indent=2) + "\n")
    print(json.dumps(evidence, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--previous", required=True)
    parser.add_argument("--target", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    options = parser.parse_args()
    try:
        qualify(options)
    except Exception as error:
        options.evidence.mkdir(parents=True, exist_ok=True)
        (options.evidence / "failure.json").write_text(json.dumps({"error": str(error)}, indent=2) + "\n")
        raise
