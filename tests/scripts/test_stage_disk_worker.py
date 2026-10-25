import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("stage_disk_worker", Path(__file__).resolve().parents[2] / "scripts/stage_disk_worker.py")
worker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(worker)
COMMIT = "a" * 40
IDENTITY = {"schemaVersion": 1, "role": "read-only-inspection-v1", "runtimeIdentifier": "linux-x64", "sourceCommit": COMMIT}


class StageDiskWorkerTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.source = self.root / "worker"
        self.app = self.root / "app"
        self.source.mkdir()
        self.app.mkdir()
        for name in worker.REQUIRED:
            (self.source / name).write_bytes(("synthetic-fixture-" + name).encode())
        (self.source / "VaultSync.DiskWorker").chmod(0o755)
        (self.source / "VaultSync.DiskWorker.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
            "tfm": "net10.0", "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.13"}]}}))
        (self.source / "VaultSync.DiskWorker.deps.json").write_text(json.dumps({"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/linux-x64"}}))
        (self.app / "VaultSync.Core.dll").write_bytes((self.source / "VaultSync.Core.dll").read_bytes())
        (self.app / "preserved.txt").write_text("untouched build fixture")

    def run_stage(self, identity=None):
        with patch.object(worker, "read_identity", return_value=IDENTITY if identity is None else identity):
            return worker.stage(self.source, self.app, COMMIT)

    def test_stages_exact_complete_closure_and_manifest_without_changing_source(self):
        before = {path.name: path.read_bytes() for path in self.source.iterdir()}
        self.assertEqual(self.run_stage(), len(before))
        folder = self.app / "DiskWorker"
        manifest = json.loads((folder / worker.MANIFEST).read_text())
        self.assertEqual(manifest["sourceCommit"], COMMIT)
        self.assertEqual(manifest["role"], "read-only-inspection-v1")
        self.assertEqual([record["name"] for record in manifest["files"]], sorted(before))
        for record in manifest["files"]:
            self.assertEqual((folder / record["name"]).read_bytes(), before[record["name"]])
            self.assertEqual(worker.digest(folder / record["name"]), record["sha256"])
        self.assertEqual({path.name: path.read_bytes() for path in self.source.iterdir()}, before)
        self.assertEqual((self.app / "preserved.txt").read_text(), "untouched build fixture")

    def test_foreign_source_role_runtime_boolean_schema_and_diagnostics_never_stage(self):
        for field, value in [("sourceCommit", "b" * 40), ("role", "restore-v1"), ("runtimeIdentifier", "linux-arm64"), ("schemaVersion", True)]:
            with self.subTest(field=field):
                with self.assertRaises(ValueError):
                    self.run_stage({**IDENTITY, field: value})
                self.assertFalse((self.app / "DiskWorker").exists())

    def test_missing_runtime_unmatched_core_and_existing_output_are_not_replaced(self):
        core = self.app / "VaultSync.Core.dll"
        original = core.read_bytes()
        core.write_bytes(b"foreign-core")
        with self.assertRaises(ValueError):
            self.run_stage()
        self.assertEqual(core.read_bytes(), b"foreign-core")
        core.write_bytes(original)
        required = self.source / "libhostfxr.so"
        required.unlink()
        with self.assertRaises(ValueError):
            self.run_stage()
        required.write_bytes(b"synthetic-runtime")
        target = self.app / "DiskWorker"
        target.mkdir()
        (target / "keep").write_text("existing output")
        with self.assertRaises(ValueError):
            self.run_stage()
        self.assertEqual((target / "keep").read_text(), "existing output")

    def test_symlinks_hardlinks_and_non_leaf_runtime_entries_refuse(self):
        alias = self.source / "alias"
        if os.name == "posix":
            alias.symlink_to(self.source / "VaultSync.Core.dll")
            with self.assertRaises(ValueError):
                self.run_stage()
            alias.unlink()
        os.link(self.source / "VaultSync.Core.dll", alias)
        with self.assertRaises(ValueError):
            self.run_stage()
        alias.unlink()
        alias.mkdir()
        with self.assertRaises(ValueError):
            self.run_stage()
        self.assertFalse((self.app / "DiskWorker").exists())

    def test_framework_dependency_wrong_rid_and_duplicate_json_refuse(self):
        runtime = self.source / "VaultSync.DiskWorker.runtimeconfig.json"
        original = runtime.read_bytes()
        runtime.write_text('{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App"}}}')
        with self.assertRaises(ValueError):
            self.run_stage()
        runtime.write_bytes(original)
        deps = self.source / "VaultSync.DiskWorker.deps.json"
        deps.write_text('{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/linux-arm64"}}')
        with self.assertRaises(ValueError):
            self.run_stage()
        with self.assertRaises(ValueError):
            worker.strict_json('{"role":"x","role":"y"}')

    def test_source_change_during_copy_is_not_published(self):
        original = worker.shutil.copyfile
        def change(source, target):
            result = original(source, target)
            Path(target).write_bytes(b"changed-after-hash")
            return result
        with patch.object(worker.shutil, "copyfile", side_effect=change):
            with self.assertRaises(ValueError):
                self.run_stage()
        self.assertFalse((self.app / "DiskWorker").exists())
        self.assertEqual(list(self.app.glob(".worker-build-*")), [])

    def identity_program(self, body):
        executable = self.root / "identity-fixture"
        executable.write_text("#!" + sys.executable + "\n" + body)
        executable.chmod(0o755)
        return executable

    def test_real_identity_pipe_is_bounded_and_does_not_forward_diagnostics(self):
        if os.name != "posix":
            with self.assertRaises(ValueError):
                worker.read_identity(self.root / "unsupported", self.root)
            return
        good = self.identity_program("import json\nprint(" + repr(json.dumps(IDENTITY)) + ")\n")
        self.assertEqual(worker.read_identity(good, self.root), IDENTITY)
        for body in ["print('x' * 513)\n", "import sys\nsys.stderr.write('private-diagnostic-sentinel')\n"]:
            with self.subTest(body=body):
                program = self.identity_program(body)
                with self.assertRaises(ValueError) as error:
                    worker.read_identity(program, self.root)
                self.assertNotIn("private-diagnostic-sentinel", str(error.exception))
        hanging = self.identity_program("import time\ntime.sleep(30)\n")
        start = time.monotonic()
        with self.assertRaises(ValueError):
            worker.read_identity(hanging, self.root, timeout=0.05)
        self.assertLess(time.monotonic() - start, 3)


if __name__ == "__main__":
    unittest.main()
