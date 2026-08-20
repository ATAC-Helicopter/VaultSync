import copy
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "release_manifest.py"

spec = importlib.util.spec_from_file_location("release_manifest", MODULE_PATH)
release_manifest = importlib.util.module_from_spec(spec)
assert spec is not None and spec.loader is not None
spec.loader.exec_module(release_manifest)


CORE_ASSETS = [
    "VaultSync-Setup-1.8.7.exe",
    "vaultsync-patch-windows.json",
    "vaultsync-patch-windows.zip",
    "VaultSync-1.8.7-macos-apple-silicon.dmg",
    "vaultsync-patch-macos-apple-silicon.json",
    "vaultsync-patch-macos-apple-silicon.zip",
    "VaultSync-1.8.7-macos-intel.dmg",
    "vaultsync-patch-macos-intel.json",
    "vaultsync-patch-macos-intel.zip",
    "VaultSync-1.8.7-linux-x64.tar.gz",
    "VaultSync-1.8.7-linux-x64.deb",
    "VaultSync-1.8.7-linux-x64.AppImage",
    "VaultSync-1.8.7-linux-arm64.tar.gz",
    "VaultSync-1.8.7-linux-arm64.deb",
]


class ReleaseManifestTests(unittest.TestCase):
    def write_assets(self, root: Path, names: list[str] = CORE_ASSETS) -> None:
        for index, name in enumerate(names):
            platform_dir = root / f"bundle-{index % 3}"
            platform_dir.mkdir(parents=True, exist_ok=True)
            (platform_dir / name).write_bytes(f"artifact:{name}".encode())

    def build(self, root: Path) -> dict[str, object]:
        return release_manifest.build_manifest(
            root,
            version="1.8.7",
            channel="stable",
            commit="a" * 40,
            repository="ATAC-Helicopter/VaultSync",
            predecessors=["1.8.6"],
        )

    def test_build_manifest_covers_exact_core_matrix_and_is_deterministic(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            self.write_assets(root)

            first = self.build(root)
            second = self.build(root)

            self.assertEqual(first, second)
            self.assertEqual(1, first["schemaVersion"])
            self.assertEqual("v1.8.7", first["release"]["tag"])
            self.assertEqual(len(CORE_ASSETS), len(first["assets"]))
            self.assertEqual(
                sorted(CORE_ASSETS, key=str.lower),
                [asset["name"] for asset in first["assets"]],
            )
            release_manifest.validate_manifest(first, asset_root=root)

    def test_build_manifest_supports_optional_linux_patches_and_store_upload(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            names = CORE_ASSETS + [
                "vaultsync-patch-linux-x64.json",
                "vaultsync-patch-linux-x64.zip",
                "vaultsync-patch-linux-arm64.json",
                "vaultsync-patch-linux-arm64.zip",
                "VaultSync-Store-1.8.7-x64.msixupload",
            ]
            self.write_assets(root, names)

            manifest = release_manifest.build_manifest(
                root,
                version="1.8.7",
                channel="stable",
                commit="b" * 40,
                repository="ATAC-Helicopter/VaultSync",
                predecessors=["1.8.6"],
                include_linux_patches=True,
                include_store_upload=True,
            )

            self.assertEqual(len(names), len(manifest["assets"]))

    def test_build_manifest_supports_full_dmg_macos_transition(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            names = [
                name
                for name in CORE_ASSETS
                if not name.startswith("vaultsync-patch-macos-")
            ]
            self.write_assets(root, names)

            manifest = release_manifest.build_manifest(
                root,
                version="1.8.7",
                channel="stable",
                commit="c" * 40,
                repository="ATAC-Helicopter/VaultSync",
                predecessors=["1.8.6"],
                omit_macos_patches=True,
            )

            self.assertEqual(len(names), len(manifest["assets"]))
            self.assertFalse(
                any(asset["packageKind"].startswith("patch-") and asset["platform"] == "macos"
                    for asset in manifest["assets"])
            )

    def test_macos_patch_omission_is_rejected_outside_transition_release(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            names = [
                name.replace("1.8.7", "1.8.8")
                for name in CORE_ASSETS
                if not name.startswith("vaultsync-patch-macos-")
            ]
            self.write_assets(root, names)

            with self.assertRaisesRegex(ValueError, "only for the 1.8.7"):
                release_manifest.build_manifest(
                    root,
                    version="1.8.8",
                    channel="stable",
                    commit="d" * 40,
                    repository="ATAC-Helicopter/VaultSync",
                    predecessors=["1.8.7"],
                    omit_macos_patches=True,
                )

    def test_build_manifest_rejects_missing_and_unexpected_assets(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            self.write_assets(root, CORE_ASSETS[:-1])
            with self.assertRaisesRegex(ValueError, "matrix mismatch"):
                self.build(root)

            (root / "notes.txt").write_text("not an asset", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Unexpected release asset"):
                self.build(root)

    def test_build_manifest_rejects_duplicate_names_and_roles(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            self.write_assets(root)
            duplicate = root / "duplicate"
            duplicate.mkdir()
            (duplicate / CORE_ASSETS[0]).write_bytes(b"duplicate")
            with self.assertRaisesRegex(ValueError, "Duplicate release asset name"):
                self.build(root)

    def test_validate_manifest_detects_tampering(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            self.write_assets(root)
            manifest = self.build(root)
            asset_name = manifest["assets"][0]["name"]
            next(root.rglob(asset_name)).write_bytes(b"tampered")

            with self.assertRaisesRegex(ValueError, "bytes do not match"):
                release_manifest.validate_manifest(manifest, asset_root=root)

    def test_validate_manifest_rejects_unsafe_url_hash_and_schema(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            self.write_assets(root)
            manifest = self.build(root)

            unsafe = copy.deepcopy(manifest)
            unsafe["assets"][0]["downloadUrl"] = "https://evil.example/download.exe"
            with self.assertRaisesRegex(ValueError, "Unsafe or inconsistent"):
                release_manifest.validate_manifest(unsafe)

            bad_hash = copy.deepcopy(manifest)
            bad_hash["assets"][0]["sha256"] = "not-a-hash"
            with self.assertRaisesRegex(ValueError, "Invalid SHA-256"):
                release_manifest.validate_manifest(bad_hash)

            unsupported = copy.deepcopy(manifest)
            unsupported["schemaVersion"] = 2
            with self.assertRaisesRegex(ValueError, "Unsupported"):
                release_manifest.validate_manifest(unsupported)

    def test_validate_published_assets_requires_exact_github_metadata(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            self.write_assets(root)
            manifest = self.build(root)
            published = [
                {
                    "name": asset["name"],
                    "size": asset["sizeBytes"],
                    "digest": f"sha256:{asset['sha256']}",
                    "url": asset["downloadUrl"],
                }
                for asset in manifest["assets"]
            ]
            published.append({"name": release_manifest.MANIFEST_NAME, "size": 1})

            release_manifest.validate_published_assets(manifest, published)
            changed = copy.deepcopy(published)
            changed[0]["size"] += 1
            with self.assertRaisesRegex(ValueError, "size differs"):
                release_manifest.validate_published_assets(manifest, changed)

            missing = published[1:]
            with self.assertRaisesRegex(ValueError, "asset set differs"):
                release_manifest.validate_published_assets(manifest, missing)

    def test_manifest_output_is_stable_and_excludes_itself(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as tmp:
            root = Path(tmp)
            self.write_assets(root)
            output = root / release_manifest.MANIFEST_NAME
            self.assertEqual(output, release_manifest.write_manifest(root, self.build(root)))
            first = output.read_bytes()
            release_manifest.write_manifest(root, self.build(root))

            self.assertEqual(first, output.read_bytes())
            payload = json.loads(first)
            self.assertNotIn(output.name, [asset["name"] for asset in payload["assets"]])


if __name__ == "__main__":
    unittest.main()
