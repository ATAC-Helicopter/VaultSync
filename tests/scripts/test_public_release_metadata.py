import copy
import importlib.util
import json
import tempfile
import unittest
import shutil
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "public_release_metadata.py"
spec = importlib.util.spec_from_file_location("public_release_metadata", MODULE_PATH)
public_release_metadata = importlib.util.module_from_spec(spec)
assert spec is not None and spec.loader is not None
spec.loader.exec_module(public_release_metadata)


class PublicReleaseMetadataTests(unittest.TestCase):
    def setUp(self) -> None:
        self.metadata = public_release_metadata.load_metadata(
            REPO_ROOT / "release" / "release-metadata.json"
        )

    def test_contract_and_render_are_deterministic(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as first_dir, tempfile.TemporaryDirectory(dir=REPO_ROOT) as second_dir:
            first = Path(first_dir)
            second = Path(second_dir)
            public_release_metadata.render(self.metadata, first)
            public_release_metadata.render(self.metadata, second)
            self.assertEqual(
                (first / "release-metadata.json").read_bytes(),
                (second / "release-metadata.json").read_bytes(),
            )
            self.assertEqual(
                (first / "store-release-metadata.json").read_bytes(),
                (second / "store-release-metadata.json").read_bytes(),
            )

    def test_output_file_rejects_path_bearing_names(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as temp_dir:
            with self.assertRaises(ValueError):
                public_release_metadata.output_file(Path(temp_dir), "../release-metadata.json")

    def test_contract_rejects_inconsistent_tag_predecessor_and_store_version(self) -> None:
        cases = []
        bad_tag = copy.deepcopy(self.metadata)
        bad_tag["activeRelease"]["tag"] = "v9.9.9"
        cases.append(bad_tag)
        bad_predecessor = copy.deepcopy(self.metadata)
        bad_predecessor["activeRelease"]["compatiblePredecessors"] = ["1.8.5"]
        cases.append(bad_predecessor)
        bad_store = copy.deepcopy(self.metadata)
        bad_store["store"]["packageVersion"] = "1.8.6.0"
        cases.append(bad_store)
        wrong_channel = copy.deepcopy(self.metadata)
        wrong_channel["activeRelease"]["channel"] = "beta" if "-" not in self.metadata["activeRelease"]["version"] else "stable"
        cases.append(wrong_channel)

        for value in cases:
            with self.subTest(value=value), tempfile.TemporaryDirectory(dir=REPO_ROOT) as temp_dir:
                path = Path(temp_dir) / "metadata.json"
                path.write_text(json.dumps(value), encoding="utf-8")
                with self.assertRaises(ValueError):
                    public_release_metadata.load_metadata(path)

    def test_release_changelog_iso_and_legacy_dates(self) -> None:
        for heading in ("## [1.8.10] - 2026-11-15", "## [1.8.10] - 15.11.2026"):
            with self.subTest(heading=heading), tempfile.TemporaryDirectory() as root:
                (Path(root) / "CHANGELOG.md").write_text(
                    "# Changelog\n" + heading + "\n## [1.8.8] - 02.09.2026\n",
                    encoding="utf-8",
                )
                self.assertEqual(
                    "1.8.10",
                    public_release_metadata._first_match(
                        Path(root), "CHANGELOG.md",
                        public_release_metadata.CHANGELOG_VERSION_HEADING_RE,
                    ),
                )

    def test_repository_consumers_match_contract(self) -> None:
        self.assertEqual([], public_release_metadata.validate_consumers(REPO_ROOT, self.metadata))

    def test_beta_keeps_numeric_binary_versions_and_stable_release_contract(self) -> None:
        metadata = copy.deepcopy(self.metadata)
        active = metadata["activeRelease"]
        base = active["version"].split("-", 1)[0]
        beta = f"{base}-beta.1"
        stable = metadata["currentStable"]["version"]
        active.update(version=beta, tag=f"v{beta}", channel="beta", stage="candidate",
                      targetDate="2026-11-18", previousVersion=stable, compatiblePredecessors=[stable])
        cli = f"""
<Version>{beta}</Version>
<PackageVersion>{beta}</PackageVersion>
<AssemblyVersion>{base}.0</AssemblyVersion>
<FileVersion>{base}.0</FileVersion>
<AssemblyInformationalVersion>{beta}</AssemblyInformationalVersion>
"""
        files = {
            "release/release-metadata.json": json.dumps(metadata),
            "src/VaultSync.UI/VaultSync.UI.csproj": f"<Version>{beta}</Version>",
            "src/VaultSync.CLI/VaultSync.CLI.csproj": cli,
            "installer/VaultSyncInstaller.iss": f'#define MyAppVersion "{beta}"',
            "packaging/VaultSync.Store/Package.appxmanifest": f'Version="{base}.0"',
            "docs/WHATS_NEW.md": f"## [{beta}]\n",
            "CHANGELOG.md": f"## [Unreleased]\n**Target version:** `{beta}`\n",
            "ROADMAP.md": f"## {base} — Recovery Horizon\n**Stable target:** 2027-03-26",
            f"docs/RELEASE_{base}.md": f"VaultSync {base}; protected release/{base}.",
            "docs/UPDATER.md": f"Update from {active['previousVersion']} to {beta}.",
            "release-metadata.json": json.dumps(public_release_metadata.build_public_metadata(metadata)),
            "index.html": '<span id="latest-release-version"></span>release-metadata.json',
        }
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for relative, content in files.items():
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8")
            actual = public_release_metadata.load_metadata(root / "release/release-metadata.json", root)
            self.assertEqual([], public_release_metadata.validate_consumers(root, actual))
            self.assertEqual(base, public_release_metadata.build_store_metadata(actual)["productVersion"])

    def test_changelog_target_does_not_fall_back_to_historical_markers(self) -> None:
        consumers = (
            "src/VaultSync.UI/VaultSync.UI.csproj", "src/VaultSync.CLI/VaultSync.CLI.csproj",
            "installer/VaultSyncInstaller.iss", "packaging/VaultSync.Store/Package.appxmanifest",
            "docs/WHATS_NEW.md",
        )
        version = self.metadata["activeRelease"]["version"]
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as temp_dir:
            root = Path(temp_dir)
            for relative in consumers:
                destination = root / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(REPO_ROOT / relative, destination)
            cases = (
                (f"## [Unreleased]\n**Target version:** `{version}`\n", True),
                (f"## [{version}] - 2026-10-04\n", True),
                (f"## [Unreleased]\n## [1.8.9] - 2026-09-16\n**Target version:** `{version}`\n", False),
                (f"## [Unreleased]\n**Target version:** `9.9.9`\n", False),
                (f"## [{version}] - Unreleased\n", False),
            )
            for text, valid in cases:
                with self.subTest(text=text):
                    (root / "CHANGELOG.md").write_text(text, encoding="utf-8")
                    errors = []
                    public_release_metadata.validate_version_consumers(root, self.metadata, errors)
                    self.assertEqual(valid, not errors, errors)


class PlannedReleaseDateTests(unittest.TestCase):
    def test_undated_planning_is_allowed_but_candidate_and_released_require_dates(self):
        stable = {"releasedDate": "2026-09-16"}
        public_release_metadata.validate_dates({"targetDate": None, "stage": "planned"}, stable)
        for stage in ("candidate", "released"):
            with self.subTest(stage=stage), self.assertRaises(ValueError):
                public_release_metadata.validate_dates({"targetDate": None, "stage": stage}, stable)
        with self.assertRaises(ValueError):
            public_release_metadata.validate_dates({"targetDate": "not-a-date", "stage": "planned"}, stable)
