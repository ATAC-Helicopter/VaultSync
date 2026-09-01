import importlib.util
import json
import os
import tempfile
import unittest
import zipfile
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "build_patch.py"

spec = importlib.util.spec_from_file_location("build_patch", MODULE_PATH)
build_patch = importlib.util.module_from_spec(spec)
assert spec is not None and spec.loader is not None
spec.loader.exec_module(build_patch)


class BuildPatchTests(unittest.TestCase):
    def test_build_patch_writes_archive_and_manifest(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            base_dir = root / "publish"
            base_dir.mkdir()
            (base_dir / "VaultSync.UI").write_bytes(b"binary")
            out_zip = root / "patches" / "patch.zip"
            out_manifest = root / "manifests" / "patch.json"

            build_patch.build_patch(
                base_dir,
                out_zip,
                out_manifest,
                "linux",
                ["1.8.0"],
                "1.8.1",
            )

            with zipfile.ZipFile(out_zip) as archive:
                self.assertEqual(archive.namelist(), ["VaultSync.UI"])
            manifest = json.loads(out_manifest.read_text(encoding="utf-8"))
            self.assertEqual(manifest["baseVersions"], ["1.8.0"])
            self.assertEqual(manifest["targetVersion"], "1.8.1")

    def test_resolve_workspace_path_rejects_escape(self) -> None:
        with self.assertRaises(ValueError):
            build_patch.resolve_workspace_path("../outside")

    def test_build_patch_allows_workspace_root_outputs(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            base_dir = root / "publish"
            base_dir.mkdir()
            (base_dir / "VaultSync.UI").write_bytes(b"binary")

            build_patch.build_patch(
                base_dir,
                root / "patch.zip",
                root / "patch.json",
                "linux",
                ["1.8.0"],
                "1.8.1",
            )

            self.assertTrue((root / "patch.zip").is_file())
            self.assertTrue((root / "patch.json").is_file())

    def test_build_patch_rejects_unqualified_multi_base_manifest(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            base_dir = root / "publish"
            base_dir.mkdir()
            (base_dir / "VaultSync.UI").write_bytes(b"binary")

            with self.assertRaisesRegex(ValueError, "requires a reference patch manifest"):
                build_patch.build_patch(
                    base_dir,
                    root / "patch.zip",
                    root / "patch.json",
                    "linux",
                    ["1.8.4", "1.8.5-Beta.1"],
                    "1.8.5",
                )

            self.assertFalse((root / "patch.zip").exists())
            self.assertFalse((root / "patch.json").exists())

    def test_build_patch_allows_overlay_safe_additional_base(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            base_dir = root / "publish"
            base_dir.mkdir()
            (base_dir / "VaultSync.UI").write_bytes(b"new")
            (base_dir / "shared.dll").write_bytes(b"shared")
            reference = root / "base.json"
            reference.write_text(
                json.dumps({"files": [{"path": "VaultSync.UI"}, {"path": "shared.dll"}]}),
                encoding="utf-8",
            )

            out_manifest = root / "patch.json"
            build_patch.build_patch(
                base_dir,
                root / "patch.zip",
                out_manifest,
                "linux",
                ["1.8.7", "1.8.2"],
                "1.8.8",
                {"1.8.2": reference},
            )

            manifest = json.loads(out_manifest.read_text(encoding="utf-8"))
            self.assertEqual(manifest["baseVersions"], ["1.8.7", "1.8.2"])

    def test_build_patch_rejects_additional_base_with_obsolete_file(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            base_dir = root / "publish"
            base_dir.mkdir()
            (base_dir / "VaultSync.UI").write_bytes(b"new")
            reference = root / "base.json"
            reference.write_text(
                json.dumps({"files": [{"path": "VaultSync.UI"}, {"path": "obsolete.dll"}]}),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "not overlay-safe"):
                build_patch.build_patch(
                    base_dir,
                    root / "patch.zip",
                    root / "patch.json",
                    "linux",
                    ["1.8.7", "1.8.4"],
                    "1.8.8",
                    {"1.8.4": reference},
                )

    def test_build_patch_omits_incompatible_optional_base(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            base_dir = root / "publish"
            base_dir.mkdir()
            (base_dir / "VaultSync.UI").write_bytes(b"new")
            reference = root / "base.json"
            reference.write_text(
                json.dumps({"files": [{"path": "VaultSync.UI"}, {"path": "obsolete.dll"}]}),
                encoding="utf-8",
            )
            out_manifest = root / "patch.json"

            build_patch.build_patch(
                base_dir,
                root / "patch.zip",
                out_manifest,
                "linux",
                ["1.8.7", "1.8.4"],
                "1.8.8",
                {"1.8.4": reference},
                skip_incompatible_bases=True,
            )

            manifest = json.loads(out_manifest.read_text(encoding="utf-8"))
            self.assertEqual(manifest["baseVersions"], ["1.8.7"])

    def test_build_patch_rejects_symlink_outside_base(self) -> None:
        if os.name == "nt":
            self.skipTest("Creating symlinks is not reliably permitted on Windows.")

        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            base_dir = root / "publish"
            base_dir.mkdir()
            outside = root / "outside.bin"
            outside.write_bytes(b"outside")
            (base_dir / "escape.bin").symlink_to(outside)

            with self.assertRaises(ValueError):
                build_patch.build_patch(
                    base_dir,
                    root / "patch.zip",
                    root / "patch.json",
                    "linux",
                    ["1.8.0"],
                    "1.8.1",
                )


if __name__ == "__main__":
    unittest.main()
