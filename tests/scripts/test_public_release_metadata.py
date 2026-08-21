import copy
import importlib.util
import json
import tempfile
import unittest
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

        for value in cases:
            with self.subTest(value=value), tempfile.TemporaryDirectory(dir=REPO_ROOT) as temp_dir:
                path = Path(temp_dir) / "metadata.json"
                path.write_text(json.dumps(value), encoding="utf-8")
                with self.assertRaises(ValueError):
                    public_release_metadata.load_metadata(path)

    def test_repository_consumers_match_contract(self) -> None:
        self.assertEqual([], public_release_metadata.validate_consumers(REPO_ROOT, self.metadata))
