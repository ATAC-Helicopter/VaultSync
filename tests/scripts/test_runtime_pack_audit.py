from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from io import StringIO
from pathlib import Path
from unittest.mock import patch


REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "runtime_pack_audit.py"

spec = importlib.util.spec_from_file_location("runtime_pack_audit", MODULE_PATH)
runtime_pack_audit = importlib.util.module_from_spec(spec)
assert spec is not None and spec.loader is not None
spec.loader.exec_module(runtime_pack_audit)


class RuntimePackAuditTests(unittest.TestCase):
    def write_repo(self, root: Path, minimum: str = "10.0.11") -> None:
        (root / "Directory.Build.props").write_text(
            "<Project><PropertyGroup>"
            f"<VaultSyncMinimumRuntimeVersion>{minimum}</VaultSyncMinimumRuntimeVersion>"
            "</PropertyGroup></Project>",
            encoding="utf-8",
        )

    def write_runtimeconfig(self, path: Path, version: str | None) -> None:
        frameworks = [] if version is None else [
            {"name": "Microsoft.NETCore.App", "version": version}
        ]
        path.write_text(
            json.dumps({"runtimeOptions": {"includedFrameworks": frameworks}}),
            encoding="utf-8",
        )

    def test_configured_minimum_reads_the_repository_property(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self.write_repo(root, "10.0.12")
            self.assertEqual("10.0.12", runtime_pack_audit.configured_minimum(root))

    def test_configured_minimum_rejects_a_missing_property(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "Directory.Build.props").write_text("<Project />", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "not configured"):
                runtime_pack_audit.configured_minimum(root)

    def test_audit_runtimeconfig_accepts_the_minimum_or_newer(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "app.runtimeconfig.json"
            self.write_runtimeconfig(path, "10.0.12")
            self.assertEqual([], runtime_pack_audit.audit_runtimeconfig(path, "10.0.11"))

    def test_audit_runtimeconfig_rejects_an_old_runtime(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "app.runtimeconfig.json"
            self.write_runtimeconfig(path, "10.0.8")
            errors = runtime_pack_audit.audit_runtimeconfig(path, "10.0.11")
            self.assertEqual(1, len(errors))
            self.assertIn("embeds Microsoft.NETCore.App 10.0.8", errors[0])

    def test_audit_runtimeconfig_requires_self_contained_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "app.runtimeconfig.json"
            self.write_runtimeconfig(path, None)
            errors = runtime_pack_audit.audit_runtimeconfig(path, "10.0.11")
            self.assertEqual(1, len(errors))
            self.assertIn("metadata is missing", errors[0])

    def test_resolve_runtimeconfig_rejects_paths_outside_allowed_roots(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as repo_tmp:
            with tempfile.TemporaryDirectory() as outside_tmp:
                path = Path(outside_tmp) / "app.runtimeconfig.json"
                self.write_runtimeconfig(path, "10.0.11")
                with patch.object(runtime_pack_audit.tempfile, "gettempdir", return_value=repo_tmp):
                    with self.assertRaisesRegex(ValueError, "must stay inside"):
                        runtime_pack_audit.resolve_runtimeconfig(path, Path(repo_tmp))

    def test_resolve_runtimeconfig_requires_the_expected_filename(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path = root / "metadata.json"
            self.write_runtimeconfig(path, "10.0.11")
            with self.assertRaisesRegex(ValueError, r"\.runtimeconfig\.json"):
                runtime_pack_audit.resolve_runtimeconfig(path, root)

    def test_main_returns_success_for_a_serviced_runtime(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            config = root / "app.runtimeconfig.json"
            self.write_repo(root)
            self.write_runtimeconfig(config, "10.0.11")
            output = StringIO()
            with patch.object(runtime_pack_audit, "REPOSITORY_ROOT", root), patch.object(
                sys, "argv", ["runtime_pack_audit.py", "--runtimeconfig", str(config)]
            ), redirect_stdout(output):
                self.assertEqual(0, runtime_pack_audit.main())
            self.assertIn("Runtime security audit passed", output.getvalue())

    def test_main_returns_failure_for_an_unserviced_runtime(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            config = root / "app.runtimeconfig.json"
            self.write_repo(root)
            self.write_runtimeconfig(config, "10.0.8")
            output = StringIO()
            with patch.object(runtime_pack_audit, "REPOSITORY_ROOT", root), patch.object(
                sys, "argv", ["runtime_pack_audit.py", "--runtimeconfig", str(config)]
            ), redirect_stderr(output):
                self.assertEqual(1, runtime_pack_audit.main())
            self.assertIn("require >= 10.0.11", output.getvalue())


if __name__ == "__main__":
    unittest.main()
