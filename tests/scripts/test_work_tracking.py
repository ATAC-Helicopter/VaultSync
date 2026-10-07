import importlib.util
import pathlib
import sys
import unittest

MODULE_PATH = pathlib.Path(__file__).resolve().parents[2] / "scripts" / "work_tracking.py"
spec = importlib.util.spec_from_file_location("work_tracking", MODULE_PATH)
work_tracking = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = work_tracking
assert spec.loader is not None
spec.loader.exec_module(work_tracking)


class WorkTrackingTests(unittest.TestCase):
    def test_slugify(self):
        self.assertEqual(work_tracking.slugify("Fix: Résumé / restore path!"), "fix-resume-restore-path")

    def test_release_family_parsing(self):
        self.assertEqual(work_tracking.parse_release_family("release/1.9.5"), (1, 9))
        self.assertEqual(work_tracking.family_code(1, 10), "110")

    def test_allocate_vs_id_within_family(self):
        work_id = work_tracking.allocate_id(
            {1893, 1896, 1897, 1975},
            prefix="VS",
            major=1,
            minor=8,
            family_digits=2,
        )
        self.assertEqual(work_id, "VS-1898")

    def test_allocate_bug_id_within_family(self):
        work_id = work_tracking.allocate_id(
            {18175, 18176, 19007},
            prefix="BUG",
            major=1,
            minor=8,
            family_digits=3,
        )
        self.assertEqual(work_id, "BUG-18177")

    def test_pr_requires_id_and_issue_link(self):
        payload = {
            "pull_request": {
                "title": "[VS-1898] Improve tracker",
                "body": "## Tracking\n\nCloses #758",
                "user": {"login": "ATAC-Helicopter"},
            }
        }
        self.assertEqual(work_tracking.validate_pr_payload(payload), [])

    def test_pr_rejects_missing_tracking(self):
        payload = {
            "pull_request": {
                "title": "Improve tracker",
                "body": "No issue here",
                "user": {"login": "ATAC-Helicopter"},
            }
        }
        errors = work_tracking.validate_pr_payload(payload)
        self.assertEqual(len(errors), 2)

    def test_bootstrap_exemption(self):
        payload = {
            "pull_request": {
                "title": "Bootstrap tracker",
                "body": "Tracking: bootstrap",
                "user": {"login": "ATAC-Helicopter"},
            }
        }
        self.assertEqual(work_tracking.validate_pr_payload(payload), [])

    def test_push_audit_is_warning_only(self):
        payload = {
            "commits": [
                {"id": "abc123456", "message": "fix: no tracking"},
                {"id": "def123456", "message": "fix(BUG-18177): tracked"},
                {"id": "ghi123456", "message": "Merge branch 'x'"},
            ]
        }
        self.assertEqual(work_tracking.audit_push_payload(payload), ["Untracked commit abc12345: fix: no tracking"])


if __name__ == "__main__":
    unittest.main()
