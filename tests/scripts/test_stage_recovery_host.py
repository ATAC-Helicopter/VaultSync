import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("stage_recovery_host", Path(__file__).resolve().parents[2] / "scripts/stage_recovery_host.py")
media = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(media)
COMMIT = "a" * 40


@unittest.skipUnless(os.name == "posix", "Linux media producer requires POSIX file identities and executable modes")
class StageRecoveryHostTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.host, self.controller, self.reader = [self.root / name for name in ("host", "controller", "reader")]
        for directory, name in ((self.host, "VaultSync.RecoveryHost"), (self.controller, "VaultSync.RecoveryController"),
                                (self.reader, "VaultSync.DiskWorker")):
            directory.mkdir()
            for leaf in (name, name + ".dll", name + ".deps.json", name + ".runtimeconfig.json", "VaultSync.Core.dll",
                         "libhostfxr.so", "libhostpolicy.so", "libcoreclr.so", "System.Private.CoreLib.dll"):
                (directory / leaf).write_bytes(b"shared-Core" if leaf == "VaultSync.Core.dll" else ("fixture-" + leaf).encode())
                (directory / leaf).chmod(0o755 if leaf == name else 0o644)
            (directory / (name + ".runtimeconfig.json")).write_text(json.dumps({"runtimeOptions": {
                "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.13"}]}}))
            (directory / (name + ".deps.json")).write_text(json.dumps({"runtimeTarget": {"name": "fixture/linux-x64"}}))
        self.launcher = self.root / "recovery-controller-launch"
        self.launcher.write_bytes(b"fixture-fixed-launcher"); self.launcher.chmod(0o755)
        self.systemd = self.root / "systemd"
        self.systemd.write_bytes(b"fixture-target-systemd"); self.systemd.chmod(0o755)
        self.output = self.root / "installation"
        records = [{"name": file.name, "size": file.stat().st_size, "sha256": media.worker.digest(file),
                    "executable": bool(file.stat().st_mode & 0o111)} for file in sorted(self.reader.iterdir())]
        self.reader_manifest = {"schemaVersion": 1, "role": "read-only-inspection-v1", "runtimeIdentifier": "linux-x64",
                                "sourceCommit": COMMIT, "files": records}
        (self.reader / "worker.installation.json").write_text(json.dumps(self.reader_manifest))
        self.original_reader = (self.reader / "worker.installation.json").read_bytes()

    @staticmethod
    def identity(program, directory):
        role = {"VaultSync.RecoveryHost": "offline-recovery-supervisor-v1", "VaultSync.RecoveryController": "recovery-controller-v1",
                "recovery-controller-launch": "recovery-controller-launch-v1"}[program.name]
        return {"schemaVersion": 1, "role": role, "runtimeIdentifier": "linux-x64", "sourceCommit": COMMIT}

    def run_stage(self, identity=None):
        with patch.object(media.worker, "read_identity", side_effect=identity or self.identity):
            return media.stage(self.host, self.controller, self.reader, self.launcher, self.systemd, self.output, COMMIT, "en")

    def test_source_bound_create_only_installation_keeps_reader_and_sources_unchanged(self):
        count = self.run_stage()
        value = media.worker.strict_json((self.output / media.MANIFEST).read_bytes())
        self.assertEqual(value["role"], "offline-recovery-media-v1")
        self.assertEqual(value["sourceCommit"], COMMIT)
        self.assertEqual(value["systemdSha256"], media.worker.digest(self.systemd))
        self.assertEqual(value["controllerMode"], "headless")
        self.assertIsNone(value["xorgSha256"])
        self.assertEqual(count, len(value["files"]))
        self.assertEqual([file["path"] for file in value["files"]], sorted(file["path"] for file in value["files"]))
        for record in value["files"]:
            self.assertEqual(media.worker.digest(self.output / record["path"]), record["sha256"])
        self.assertEqual((self.output / "host/DiskWorker/worker.installation.json").read_bytes(), self.original_reader)
        self.assertEqual((self.reader / "worker.installation.json").read_bytes(), self.original_reader)
        self.assertEqual((self.host / "VaultSync.Core.dll").read_bytes(), b"shared-Core")
        with self.assertRaises(ValueError):
            self.run_stage()

    def test_graphical_dependencies_are_source_bound_and_catalog_nesting_is_exact(self):
        catalogs = self.controller / "Localization"
        catalogs.mkdir()
        for source in (media.REPOSITORY / "Localization").glob("strings.*.json"):
            (catalogs / source.name).write_bytes(source.read_bytes())
        display = self.root / "recovery-display-launch"
        display.write_bytes(b"fixture-display-bootstrap"); display.chmod(0o755)
        xorg = self.root / "Xorg"
        xorg.write_bytes(b"fixture-target-Xorg"); xorg.chmod(0o755)
        def identity(program, directory):
            if program == display:
                return {"schemaVersion": 1, "role": "recovery-display-launch-v1", "runtimeIdentifier": "linux-x64", "sourceCommit": COMMIT}
            return self.identity(program, directory)
        with patch.object(media.worker, "read_identity", side_effect=identity):
            media.stage(self.host, self.controller, self.reader, self.launcher, self.systemd, self.output, COMMIT, "en", "graphical", display, xorg)
        value = media.worker.strict_json((self.output / media.MANIFEST).read_bytes())
        self.assertEqual(value["controllerMode"], "graphical")
        self.assertEqual(value["xorgSha256"], media.worker.digest(xorg))
        self.assertEqual((self.output / "boot/vaultsync-recovery.service").read_bytes(),
                         (media.REPOSITORY / "packaging/recovery/vaultsync-recovery-graphical.service").read_bytes())
        self.assertEqual(len(list((self.output / "controller/Localization").iterdir())), 19)

    def test_missing_graphical_dependencies_and_arbitrary_nested_directories_refuse_before_creation(self):
        with self.assertRaises(ValueError):
            media.stage(self.host, self.controller, self.reader, self.launcher, self.systemd, self.output, COMMIT, "en", "graphical")
        (self.controller / "unexpected").mkdir()
        with self.assertRaises(ValueError):
            self.run_stage()
        self.assertFalse(self.output.exists())

    def test_existing_output_and_outside_build_root_refuse_before_execution(self):
        self.output.mkdir()
        preserved = self.output / "original.backup"
        preserved.write_bytes(b"preserve-existing-data")
        with patch.object(media.worker, "read_identity") as execute:
            with self.assertRaises(ValueError):
                media.stage(self.host, self.controller, self.reader, self.launcher, self.systemd, self.output, COMMIT, "en")
            execute.assert_not_called()
        self.assertEqual(preserved.read_bytes(), b"preserve-existing-data")
        with patch.object(media.worker, "allowed_build_roots", return_value=(self.host,)), \
                patch.object(media.worker, "read_identity") as execute:
            with self.assertRaises(ValueError):
                media.stage(self.host, self.controller, self.reader, self.launcher, self.systemd, self.root / "other", COMMIT, "en")
            execute.assert_not_called()

    def test_symlink_hardlink_and_shared_writable_inputs_refuse(self):
        linked = self.host / "alias.dll"
        linked.symlink_to(self.host / "VaultSync.Core.dll")
        with self.assertRaises(ValueError):
            self.run_stage()
        linked.unlink()
        os.link(self.host / "VaultSync.Core.dll", linked)
        with self.assertRaises(ValueError):
            self.run_stage()
        linked.unlink()
        (self.host / "VaultSync.Core.dll").chmod(0o664)
        with self.assertRaises(ValueError):
            self.run_stage()
        self.assertFalse(self.output.exists())

    def test_wrong_build_role_core_and_reader_manifest_fail_closed(self):
        def wrong(program, directory):
            value = self.identity(program, directory)
            value["role"] = "read-only-inspection-v1"
            return value
        with self.assertRaises(ValueError):
            self.run_stage(wrong)
        (self.controller / "VaultSync.Core.dll").write_bytes(b"different-build")
        with self.assertRaises(ValueError):
            self.run_stage()
        (self.controller / "VaultSync.Core.dll").write_bytes(b"shared-Core")
        manifest = self.reader / "worker.installation.json"
        manifest.write_text(manifest.read_text().replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1'))
        with self.assertRaises(ValueError):
            self.run_stage()
        self.assertFalse(self.output.exists())

    def test_framework_dependent_runtime_is_not_packaged(self):
        (self.host / "VaultSync.RecoveryHost.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
            "framework": {"name": "Microsoft.NETCore.App", "version": "10.0.13"}}}))
        with self.assertRaises(ValueError):
            self.run_stage()
        self.assertFalse(self.output.exists())

    def test_failed_copy_removes_only_new_owned_tree_and_preserves_inputs(self):
        with patch.object(media.shutil, "copyfileobj", side_effect=OSError("fixture-copy-failed")):
            with self.assertRaises(OSError):
                self.run_stage()
        self.assertFalse(self.output.exists())
        self.assertEqual((self.host / "VaultSync.Core.dll").read_bytes(), b"shared-Core")
        self.assertEqual((self.reader / "worker.installation.json").read_bytes(), self.original_reader)


if __name__ == "__main__":
    unittest.main()
