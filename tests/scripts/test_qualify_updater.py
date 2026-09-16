import argparse
import importlib.util
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
