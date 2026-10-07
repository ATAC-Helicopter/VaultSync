import importlib.util
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[2] / "scripts/changelog.py"
spec = importlib.util.spec_from_file_location("vaultsync_changelog", MODULE_PATH)
changelog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(changelog)


VALID = """# Changelog

The format follows https://keepachangelog.com/en/1.1.0/.

## [Unreleased]

**Target version:** `1.9.0`.

### Added

- [VS-1917] Added a review draft; format approval remains pending.

### Fixed

- [BUG-19001] Require explicit confirmation before removing unattended projects.

## [1.8.9] - 2026-09-16

### Changed

- [VS-1895] Refreshed coordinated dependencies.

## [0.9.7.3] - 2025-12-05

### Fixed

- Corrected elevated patch launches.

[Unreleased]: https://example.test/compare/stable...work
[1.8.9]: https://example.test/releases/1.8.9
[0.9.7.3]: https://example.test/releases/0.9.7.3
"""


class ChangelogTests(unittest.TestCase):
    def test_preserves_legacy_versions_and_accepts_short_entries(self):
        self.assertEqual([], changelog.validate(VALID, "1.9.0"))
        self.assertEqual("1.9.0", changelog.active_version(VALID))

    def test_rejects_target_drift_without_guessing_from_history(self):
        self.assertTrue(changelog.validate(VALID, "1.9.1"))
        missing = VALID.replace("**Target version:** `1.9.0`.\n", "")
        self.assertIsNone(changelog.active_version(missing))
        self.assertTrue(changelog.validate(missing, "1.9.0"))

    def test_rejects_nonstandard_categories_empty_groups_and_duplicates(self):
        for changed in (
            VALID.replace("### Added", "### Planning"),
            VALID.replace("### Fixed\n\n- [BUG-19001]", "### Added\n\n- [BUG-19001]"),
            VALID.replace("### Added\n", "### Removed\n\n### Added\n", 1),
            VALID.replace("### Added", "### Security"),
        ):
            with self.subTest(changed=changed):
                self.assertTrue(changelog.validate(changed, "1.9.0"))

    def test_rejects_invalid_dates_and_missing_or_duplicate_versions(self):
        for changed in (
            VALID.replace("2026-09-16", "16.09.2026"),
            VALID.replace("2026-09-16", "2026-02-30"),
            VALID.replace("[1.8.9]: https://example.test/releases/1.8.9\n", ""),
            VALID.replace("## [0.9.7.3]", "## [1.8.9]"),
            VALID.replace("## [Unreleased]", "## [1.9.0] - Unreleased"),
        ):
            with self.subTest(changed=changed):
                self.assertTrue(changelog.validate(changed, "1.9.0"))

    def test_length_limit_excludes_the_id_and_rejects_hidden_detail(self):
        entry = " ".join(["word"] * 22)
        text = VALID.replace("Added a review draft; format approval remains pending.", entry)
        self.assertEqual([], changelog.validate(text, "1.9.0"))
        self.assertTrue(changelog.validate(text.replace(entry, entry + " extra"), "1.9.0"))
        self.assertTrue(changelog.validate(text.replace(entry, entry + "\n  hidden detail"), "1.9.0"))


if __name__ == "__main__":
    unittest.main()
