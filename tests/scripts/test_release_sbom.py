import json
import tempfile
import unittest
from pathlib import Path

from scripts import release_sbom


class ReleaseSbomTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.manifest_path = self.root / "vaultsync-release-manifest.json"
        self.assets_path = self.root / "project.assets.json"
        self.output = self.root / "sboms"
        self.asset = {
            "name": "VaultSync-1.8.7-linux-x64.tar.gz",
            "platform": "linux",
            "architecture": "x64",
            "packageKind": "archive",
            "sizeBytes": 42,
            "sha256": "a" * 64,
            "downloadUrl": "https://github.com/ATAC-Helicopter/VaultSync/releases/download/v1.8.7/VaultSync-1.8.7-linux-x64.tar.gz",
        }
        self.manifest_path.write_text(json.dumps({
            "schemaVersion": 1,
            "release": {
                "version": "1.8.7",
                "channel": "stable",
                "tag": "v1.8.7",
                "commit": "b" * 40,
                "repository": "ATAC-Helicopter/VaultSync",
                "compatiblePredecessors": ["1.8.6"],
            },
            "assets": [self.asset, {
                **self.asset,
                "name": "vaultsync-patch-linux-x64.zip",
                "packageKind": "patch-archive",
                "sha256": "c" * 64,
            }],
        }), encoding="utf-8")
        self.assets_path.write_text(json.dumps({
            "libraries": {
                "Dapper/2.1.66": {"type": "package"},
                "VaultSync.Core/1.0.0": {"type": "project"},
            }
        }), encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()

    def test_generate_creates_one_valid_sbom_per_self_contained_artifact(self):
        release_sbom.generate(
            self.manifest_path,
            self.output,
            self.assets_path,
            "2026-08-17T12:00:00Z",
        )

        release_sbom.validate(self.manifest_path, self.output)
        index = json.loads((self.output / "vaultsync-release-sbom-index.json").read_text())
        self.assertEqual([self.asset["name"]], [entry["artifact"] for entry in index["sboms"]])
        document = json.loads((self.output / index["sboms"][0]["sbom"]).read_text())
        self.assertEqual("SPDX-2.3", document["spdxVersion"])
        self.assertTrue(any(package["name"] == "Dapper" for package in document["packages"]))
        self.assertIn(self.asset["sha256"], (self.output / "vaultsync-release-subjects.sha256").read_text())

    def test_validate_rejects_sbom_not_bound_to_manifest_digest(self):
        release_sbom.generate(self.manifest_path, self.output, None, "2026-08-17T12:00:00Z")
        index = json.loads((self.output / "vaultsync-release-sbom-index.json").read_text())
        path = self.output / index["sboms"][0]["sbom"]
        document = json.loads(path.read_text())
        document["packages"][0]["checksums"][0]["checksumValue"] = "d" * 64
        path.write_text(json.dumps(document), encoding="utf-8")

        with self.assertRaisesRegex(ValueError, "not bound"):
            release_sbom.validate(self.manifest_path, self.output)

    def test_dependency_loading_selects_the_requested_runtime_graph(self):
        assets_dir = self.root / "assets"
        assets_dir.mkdir()
        (assets_dir / "linux-x64.json").write_text(json.dumps({
            "libraries": {
                "Shared/1.0.0": {"type": "package"},
                "LinuxOnly/2.0.0": {"type": "package"},
                "OtherRuntime/3.0.0": {"type": "package"},
            },
            "targets": {
                "net10.0/linux-x64": {
                    "Shared/1.0.0": {},
                    "LinuxOnly/2.0.0": {},
                }
            },
        }), encoding="utf-8")

        packages = release_sbom.load_nuget_packages(assets_dir, "linux-x64")

        self.assertEqual([("LinuxOnly", "2.0.0"), ("Shared", "1.0.0")], packages)

    def test_generate_rejects_output_outside_approved_root(self):
        outside = self.root.parent / "outside-sboms"

        with self.assertRaisesRegex(ValueError, "SBOM output path must be inside"):
            release_sbom.generate(
                self.manifest_path,
                outside,
                None,
                "2026-08-17T12:00:00Z",
                self.root,
            )

    def test_generate_rejects_asset_name_with_path_components(self):
        manifest = json.loads(self.manifest_path.read_text())
        manifest["assets"][0]["name"] = "../escaped.json"
        self.manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

        with self.assertRaisesRegex(ValueError, "plain file name"):
            release_sbom.generate(
                self.manifest_path,
                self.output,
                None,
                "2026-08-17T12:00:00Z",
                self.root,
            )

    def test_validate_rejects_index_path_traversal(self):
        release_sbom.generate(self.manifest_path, self.output, None, "2026-08-17T12:00:00Z")
        index_path = self.output / "vaultsync-release-sbom-index.json"
        index = json.loads(index_path.read_text())
        index["sboms"][0]["sbom"] = "../outside.spdx.json"
        index_path.write_text(json.dumps(index), encoding="utf-8")

        with self.assertRaisesRegex(ValueError, "plain file name"):
            release_sbom.validate(self.manifest_path, self.output, self.root)


if __name__ == "__main__":
    unittest.main()
