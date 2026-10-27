import importlib.util
import pathlib
import sys
import unittest
import tempfile
from unittest.mock import patch

MODULE_PATH = pathlib.Path(__file__).resolve().parents[2] / "scripts" / "work_tracking.py"
spec = importlib.util.spec_from_file_location("work_tracking", MODULE_PATH)
work_tracking = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = work_tracking
assert spec.loader is not None
spec.loader.exec_module(work_tracking)


class WorkTrackingTests(unittest.TestCase):
    def test_family_uses_active_metadata_and_explicit_override(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "release").mkdir()
            (root / "release/release-metadata.json").write_text('{"activeRelease":{"version":"1.9.5"}}')
            self.assertEqual(work_tracking.infer_release_family(root, "docs/package-guide", None), (1, 9))
            self.assertEqual(work_tracking.infer_release_family(root, "release/1.8.9", None), (1, 8))
            self.assertEqual(work_tracking.infer_release_family(root, "docs/package-guide", "1.10"), (1, 10))
            (root / "release/release-metadata.json").write_text('{"activeRelease":null}')
            with self.assertRaises(work_tracking.TrackingError):
                work_tracking.infer_release_family(root, "work/preparation", None)

    def test_linked_unreleased_and_legacy_family(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "CHANGELOG.md").write_text('## [Unreleased]\n\n**Target version:** `1.9.0`.\n## [1.8.9] - 2026-09-16\n')
            self.assertEqual(work_tracking.infer_release_family(root, "work/preparation", None), (1, 9))
            (root / "CHANGELOG.md").write_text('## [1.8.9] - Unreleased\n')
            self.assertEqual(work_tracking.infer_release_family(root, "work/preparation", None), (1, 8))

    def test_remote_id_audit_paginates_and_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(work_tracking, "github_repo", return_value="owner/repo"), patch.object(work_tracking, "run") as execute:
            root = pathlib.Path(directory)
            execute.return_value = work_tracking.CommandResult(stdout='[[{"title":"VS-1990"}],[{"body":"VS-1993"}]]', stderr='', returncode=0)
            self.assertEqual(work_tracking.collect_used_ids(root, {"planning_files":[]}, "VS"), {1990, 1993})
            self.assertIn("--paginate", execute.call_args.args[0])
            for response in [work_tracking.CommandResult(stdout='', stderr='unavailable', returncode=1), work_tracking.CommandResult(stdout='{"message":"bad"}', stderr='', returncode=0)]:
                execute.return_value = response
                with self.assertRaises(work_tracking.TrackingError):
                    work_tracking.collect_used_ids(root, {"planning_files":[]}, "VS")

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
