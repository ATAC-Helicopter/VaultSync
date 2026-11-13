"""No-network policy regression cases for issue auto-triage."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from issue_auto_triage import (classify_kind, release_family, owning_id,
                               target_status, clean_title)
from work_tracking import TrackingError


class IssueAutoTriageTests(unittest.TestCase):
    def test_issue_form_bug(self):
        self.assertEqual(classify_kind({"bug", "release:1.8.x"}), "bug")

    def test_issue_form_feature(self):
        self.assertEqual(classify_kind({"enhancement"}), "feature")

    def test_other_vs_types(self):
        for kind in ("change", "security", "maintenance"):
            self.assertEqual(classify_kind({f"kind:{kind}"}), kind)

    def test_unclassified_support_is_not_guessed(self):
        self.assertIsNone(classify_kind({"question"}))

    def test_conflicting_kind_fails_closed(self):
        with self.assertRaises(TrackingError):
            classify_kind({"bug", "enhancement"})

    def test_exact_release_only(self):
        self.assertEqual(release_family({"release:1.9.0"}), (1, 9))
        self.assertIsNone(release_family({"release:1.9.x"}))
        self.assertIsNone(release_family({"release:1.8.x", "release:1.9.0"}))
        self.assertIsNone(release_family(set()))

    def test_title_id_and_form_prefix(self):
        self.assertEqual(owning_id("BUG-19047: startup failure"), "BUG-19047")
        self.assertEqual(owning_id("VS-1999: feature"), "VS-1999")
        self.assertIsNone(owning_id("Compare with BUG-19047"))
        self.assertEqual(clean_title("[Bug]: File left behind"), "File left behind")

    def test_status_is_preserved(self):
        self.assertEqual(target_status({"status:in-progress"}, "OPEN"), "status:in-progress")
        self.assertEqual(target_status(set(), "OPEN"), "status:todo")
        self.assertEqual(target_status({"status:done"}, "OPEN"), "status:todo")
        self.assertEqual(target_status({"status:in-progress"}, "CLOSED"), "status:done")

    def test_multiple_statuses_fail_closed(self):
        with self.assertRaises(TrackingError):
            target_status({"status:todo", "status:in-progress"}, "OPEN")


if __name__ == "__main__":
    unittest.main()
