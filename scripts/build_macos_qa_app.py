#!/usr/bin/env python3
"""Create an isolated, ad-hoc signed macOS UI validation app; never a release asset."""
import argparse
import json
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

from macos_bundle_version import bundle_versions


REQUIRED = ("VaultSync.UI", "VaultSync.UI.dll", "VaultSync.Core.dll",
            "VaultSync.UI.runtimeconfig.json", "libcoreclr.dylib",
            "libhostfxr.dylib", "libhostpolicy.dylib", "System.Private.CoreLib.dll")
MAX_BYTES = 2 * 1024 * 1024 * 1024


def sandbox_entitlements():
    # Avalonia's NSApplication initialization registers with this precise service.
    # No filesystem exceptions, network access, loader overrides or broad IPC grant.
    return {"com.apple.security.app-sandbox": True,
            "com.apple.security.cs.allow-jit": True,
            "com.apple.security.temporary-exception.mach-lookup.global-name":
                ["com.apple.coreservices.launchservicesd"]}


def validate(publish_dir, output, identifier, version):
    if not re.fullmatch(r"dev\.vaultsync\.(?:qa|recovery)\.[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*", identifier):
        raise ValueError("Use a distinct dev.vaultsync.qa.* or dev.vaultsync.recovery.* identifier")
    if len(identifier) > 200:
        raise ValueError("QA identifier exceeds its bound")
    bundle_versions(version)
    source = Path(publish_dir)
    target = Path(output)
    if source.is_symlink() or not source.is_dir():
        raise ValueError("An existing regular publish directory is required")
    if target.exists() or target.is_symlink() or target.suffix != ".app":
        raise ValueError("A new .app output is required; existing artifacts are never replaced")
    source = source.resolve(strict=True)
    parent = target.parent.resolve(strict=True)
    if parent.is_relative_to(source):
        raise ValueError("QA output must stay outside the publish input")
    total = 0
    for count, path in enumerate(source.rglob("*"), 1):
        if count > 8192 or path.is_symlink() or not (path.is_dir() or path.is_file()):
            raise ValueError("Publish inputs must be bounded regular files/directories without aliases")
        if path.is_file():
            total += path.stat().st_size
            if total > MAX_BYTES:
                raise ValueError("Publish inputs exceed the QA package bound")
    if any(not (source / name).is_file() for name in REQUIRED):
        raise ValueError("A complete self-contained macOS publish is required")
    if (source / "VaultSync.UI").stat().st_mode & 0o111 == 0:
        raise ValueError("The QA launcher must retain its executable permissions")
    with (source / "VaultSync.UI").open("rb") as stream:
        if stream.read(4) not in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
            raise ValueError("The QA launcher must be a macOS Mach-O executable")
    config_path = source / "VaultSync.UI.runtimeconfig.json"
    if config_path.stat().st_size > 65536:
        raise ValueError("Runtime configuration exceeds its bound")
    config = json.loads(config_path.read_text())
    frameworks = config.get("runtimeOptions", {}).get("includedFrameworks", [])
    runtime = next((entry.get("version", "") for entry in frameworks
                    if entry.get("name") == "Microsoft.NETCore.App"), "")
    minimum = ET.parse(Path(__file__).resolve().parents[1] / "Directory.Build.props").findtext(
        ".//VaultSyncMinimumRuntimeVersion")
    if not re.fullmatch(r"\d+\.\d+\.\d+", runtime) or tuple(map(int, runtime.split("."))) < tuple(map(int, minimum.split("."))):
        raise ValueError("A self-contained security-serviced runtime is required")
    return source, parent / target.name


def build(publish_dir, output, identifier, version):
    if sys.platform != "darwin":
        raise ValueError("QA signing must run on macOS")
    source, target = validate(publish_dir, output, identifier, version)
    short_version, build_version = bundle_versions(version)
    # Exclusive reservation precedes all writes. Failure removes only this new,
    # builder-owned directory; caller sources and previous packages stay untouched.
    target.mkdir(mode=0o700)
    try:
        contents = target / "Contents"
        shutil.copytree(source, contents / "MacOS")
        profile = Path.home() / "Library" / "Containers" / identifier / "Data" / "Library" / "Application Support" / "VaultSyncQA"
        info = {"CFBundleIdentifier": identifier, "CFBundleName": "VaultSync Recovery QA",
                "CFBundleDisplayName": "VaultSync Recovery QA", "CFBundleExecutable": "VaultSync.UI",
                "CFBundlePackageType": "APPL", "CFBundleVersion": build_version,
                "CFBundleShortVersionString": short_version, "LSMinimumSystemVersion": "12.0",
                "NSHighResolutionCapable": True, "LSEnvironment": {"VAULTSYNC_CONFIG_DIR": str(profile)}}
        (contents / "Info.plist").write_bytes(plistlib.dumps(info))
        resources = contents / "Resources"
        resources.mkdir()
        entitlements = resources / "qa-signing.plist"
        entitlements.write_bytes(plistlib.dumps(sandbox_entitlements()))
        # No caller-controlled command arguments: codesign derives the validated
        # identity from Info.plist. The exact reserved bundle is the working
        # directory; both the entitlement path and bundle operand are literals.
        subprocess.run(["/usr/bin/codesign", "--force", "--deep", "--sign", "-",
                        "--entitlements", "Contents/Resources/qa-signing.plist", "."],
                       cwd=target, check=True, timeout=120,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        # Keep the signed resource tree unchanged after sealing it.
        subprocess.run(["/usr/bin/codesign", "--verify", "--deep", "--strict", "."],
                       cwd=target, check=True, timeout=120,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        return {"app": str(target), "identifier": identifier, "privateProfile": str(profile),
                "purpose": "private-macos-ui-validation", "signing": "ad-hoc; publisher signing unqualified",
                "sandbox": True, "networkEntitlement": False, "releaseAsset": False}
    except BaseException:
        shutil.rmtree(target)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-dir", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--bundle-id", required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(build(args.publish_dir, args.output, args.bundle_id, args.version), indent=2))
    except (ValueError, OSError, subprocess.SubprocessError, ET.ParseError) as error:
        parser.exit(2, f"QA package failed: {error}\n")


if __name__ == "__main__":
    main()
