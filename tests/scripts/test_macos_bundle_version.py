"""Release ordering and invalid-input checks for macOS bundle metadata."""

import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
from macos_bundle_version import bundle_versions  # noqa: E402


class MacosBundleVersionTests(unittest.TestCase):
    def test_beta_builds_precede_stable_and_next_patch(self):
        short_first, first = bundle_versions("1.9.0-beta.1")
        short_last, last = bundle_versions("1.9.0-beta.998")
        short_stable, stable = bundle_versions("1.9.0")
        _, next_beta = bundle_versions("1.9.1-beta.1")

        self.assertEqual((short_first, short_last, short_stable), ("1.9.0",) * 3)
        self.assertEqual((first, last, stable, next_beta),
                         ("1.9.1", "1.9.998", "1.9.999", "1.9.1001"))

    def test_predecessor_and_patch_ordering_remain_monotonic(self):
        versions = ["1.8.9", "1.9.0-beta.1", "1.9.0", "1.9.1-beta.1", "1.9.1"]
        builds = [tuple(map(int, bundle_versions(version)[1].split("."))) for version in versions]
        self.assertEqual(builds, sorted(builds))
        self.assertEqual(len(set(builds)), len(builds))

    def test_unmapped_or_ambiguous_versions_fail_closed(self):
        for version in ("1.9", "v1.9.0", "1.9.0-beta.0", "1.9.0-beta.999",
                        "1.9.0-rc.1", "1.9.0+build.1", "1.9.0-beta.01",
                        "01.9.0", "1.9.0<script>"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                bundle_versions(version)


if __name__ == "__main__":
    unittest.main()
