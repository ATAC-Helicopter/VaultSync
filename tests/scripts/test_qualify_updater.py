import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock


spec = importlib.util.spec_from_file_location(
    "qualify_updater", Path(__file__).resolve().parents[2] / "scripts/qualify_updater.py")
qualify_updater = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qualify_updater)


class UpdaterQualificationTests(unittest.TestCase):
    def test_refuses_to_install_on_developer_host(self):
        with mock.patch.dict(os.environ, {"GITHUB_ACTIONS": "false"}):
            with self.assertRaisesRegex(RuntimeError, "disposable"):
                qualify_updater.qualify(argparse.Namespace())

    def test_full_payload_verification_detects_tampering(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            payload = root / "app.dll"
            payload.write_bytes(b"application")
            manifest = {"files": [{"path": "app.dll", "size": payload.stat().st_size,
                                    "sha256": qualify_updater.sha256(payload)}]}
            qualify_updater.verify_payload(root, manifest)
            payload.write_bytes(b"tampered")
            with self.assertRaisesRegex(ValueError, "mismatch"):
                qualify_updater.verify_payload(root, manifest)

    def test_payload_cannot_escape_installation(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ValueError, "escapes"):
                qualify_updater.verify_payload(Path(temporary), {"files": [{"path": "../outside"}]})

    def test_snapshot_detects_file_addition_and_removal(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.assertEqual({}, qualify_updater.snapshot(root))
            payload = root / "app.dll"
            payload.write_bytes(b"application")
            self.assertEqual({"app.dll": qualify_updater.sha256(payload)},
                             qualify_updater.snapshot(root))
            payload.unlink()
            self.assertEqual({}, qualify_updater.snapshot(root))

    @mock.patch.object(qualify_updater.subprocess, "run")
    def test_windows_installer_failure_is_not_success(self, run):
        run.return_value.returncode = 5
        with self.assertRaisesRegex(RuntimeError, "installer failed"):
            qualify_updater.install_windows(Path("setup.exe"), Path("install"), Path("install.log"))
        self.assertIn("/NORESTART", run.call_args.args[0])

    @mock.patch.object(qualify_updater.subprocess, "Popen")
    def test_real_helper_invocation_keeps_paths_as_separate_arguments(self, popen):
        qualify_updater.apply(Path("helper path"), Path("archive path"), Path("manifest path"),
                              Path("install path"), {}, 42)
        self.assertEqual(["helper path", "--apply-patch", "archive path", "manifest path",
                          "install path", "--headless-patch", "--waitpid=42"],
                         popen.call_args.args[0])

    def test_released_asset_download_requires_matching_github_digest(self):
        payload = b"released package"
        asset = {"name": "package.exe", "size": len(payload),
                 "digest": "sha256:" + hashlib.sha256(payload).hexdigest(),
                 "browser_download_url":
                 "https://github.com/ATAC-Helicopter/VaultSync/releases/download/v1.8.8/package.exe"}
        with tempfile.TemporaryDirectory() as temporary:
            target = Path(temporary) / "package.exe"
            for downloaded, succeeds in ((payload, True), (b"tampered", False)):
                with mock.patch.object(qualify_updater.urllib.request, "urlopen", side_effect=[
                        io.BytesIO(json.dumps({"assets": [asset]}).encode()), io.BytesIO(downloaded)]):
                    if succeeds:
                        qualify_updater.download_base("1.8.8", "package.exe", target)
                        self.assertEqual(payload, target.read_bytes())
                    else:
                        with self.assertRaisesRegex(ValueError, "integrity"):
                            qualify_updater.download_base("1.8.8", "package.exe", target)

    def test_qualification_orchestration_on_each_native_host(self):
        for system in ("Windows", "Linux", "Darwin"):
            with self.subTest(system=system), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                assets = root / "assets"
                assets.mkdir()
                suffix = {"Windows": "windows", "Linux": "linux-x64",
                          "Darwin": "macos-apple-silicon"}[system]
                payload = b"candidate archive"
                (assets / f"vaultsync-patch-{suffix}.zip").write_bytes(payload)
                manifest = {"targetVersion": "1.8.9", "baseVersions": ["1.8.8"],
                            "archiveSize": len(payload), "files": [],
                            "archiveSha256": hashlib.sha256(payload).hexdigest()}
                (assets / f"vaultsync-patch-{suffix}.json").write_text(json.dumps(manifest))
                (assets / "VaultSync-Setup-1.8.9.exe").touch()
                (assets / "VaultSync-1.8.9-linux-x64.deb").touch()

                def prepare_base(test_root, *unused):
                    base = test_root / "Base.app"
                    base.mkdir()
                    (base / "app.dll").write_bytes(b"old application")
                    return base

                rejected = mock.Mock()
                rejected.wait.return_value = 1
                installed = mock.Mock()
                installed.wait.return_value = 0
                installed.poll.return_value = None
                parent = mock.Mock(pid=42)
                parent.poll.return_value = 0
                running = mock.Mock(pid=123)
                running.poll.return_value = None
                with contextlib.ExitStack() as stack:
                    stack.enter_context(mock.patch.dict(os.environ,
                        {"GITHUB_ACTIONS": "true", "RUNNER_TEMP": temporary}))
                    stack.enter_context(mock.patch.object(qualify_updater.platform, "system", return_value=system))
                    stack.enter_context(mock.patch.object(qualify_updater.platform, "machine", return_value="arm64"))
                    stack.enter_context(mock.patch.object(qualify_updater, "prepare_base", side_effect=prepare_base))
                    stack.enter_context(mock.patch.object(qualify_updater, "download_base"))
                    stack.enter_context(mock.patch.object(qualify_updater, "snapshot", return_value={}))
                    stack.enter_context(mock.patch.object(qualify_updater, "verify_payload"))
                    stack.enter_context(mock.patch.object(qualify_updater, "apply", side_effect=[rejected, rejected, installed]))
                    stack.enter_context(mock.patch.object(qualify_updater.subprocess, "Popen", side_effect=[parent, running]))
                    stack.enter_context(mock.patch.object(qualify_updater.subprocess, "run", return_value=mock.Mock(returncode=0)))
                    stack.enter_context(mock.patch.object(qualify_updater.time, "sleep"))
                    stack.enter_context(mock.patch.object(qualify_updater.os, "killpg"))
                    stack.enter_context(mock.patch("builtins.print"))
                    options = argparse.Namespace(assets=assets, previous="1.8.8", target="1.8.9",
                                                 evidence=root / "evidence")
                    qualify_updater.qualify(options)
                result = json.loads((options.evidence / "updater-qualification.json").read_text())
                self.assertEqual(system, result["platform"])
                self.assertIn("external user data preserved", result["checks"])
                self.assertGreaterEqual(len(result["checks"]), 5)
