"""No-network policy regression cases for issue auto-triage."""
import sys
import contextlib
import copy
import io
import json
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from issue_auto_triage import (classify_kind, classify_form_title,
                               required_assignee, release_family, owning_id,
                               target_status, clean_title)
from work_tracking import TrackingError
import issue_auto_triage as triage


class IssueAutoTriageTests(unittest.TestCase):
    def test_triage_mutations_preserve_ownership_and_fail_closed(self):
        root = Path(__file__).resolve().parents[2]
        issue = {"number": 99, "title": "[Bug]: recovery fails", "state": "OPEN",
                 "assignees": [{"login": "existing-owner"}], "milestone": None,
                 "labels": [{"name": label} for label in (
                     "bug", "release:1.9.0", "priority:p1", "status:in-progress")]}
        available = {"bug", "kind:bug", "priority:p1", "priority:p2", "status:in-progress", "status:done"}
        with patch.object(triage, "repo_root", return_value=root), \
                patch.object(triage, "github_repo", return_value="owner/repo"), \
                patch.object(triage, "labels_available", return_value=available), \
                patch.object(triage, "collect_used_ids", return_value={19051}) as audit, \
                patch.object(triage, "run") as command, contextlib.redirect_stdout(io.StringIO()):
            command.return_value.stdout = json.dumps(issue)
            triage.run_triage(99)
            edits = command.call_args_list[-1].args[0]
            self.assertIn("BUG-19052: recovery fails", edits)
            self.assertEqual(edits[edits.index("--milestone") + 1], "1.9.0")
            self.assertNotIn("--add-assignee", edits)
            self.assertNotIn("--remove-label", edits)
            self.assertNotIn("priority:p2", edits)
            for change in ({"title": "[Feature]: conflicting form"},
                           {"milestone": {"title": "1.9.1"}},
                           {"title": "VS-1999: wrong owning ID"},
                           {"labels": issue["labels"] + [{"name": "priority:p2"}]}):
                invalid = {**copy.deepcopy(issue), **change}
                command.reset_mock()
                command.return_value.stdout = json.dumps(invalid)
                with self.subTest(change=change), self.assertRaises(TrackingError):
                    triage.run_triage(99)
                self.assertEqual(command.call_count, 1, "must refuse before issuing an edit")
            command.reset_mock()
            command.return_value.stdout = json.dumps(issue)
            audit.side_effect = TrackingError("incomplete remote allocation audit")
            with self.assertRaises(TrackingError):
                triage.run_triage(99)
            self.assertEqual(command.call_count, 1)

    def test_pending_reconciliation_is_sorted_bounded_and_conservative(self):
        def item(number, title="Unnumbered", state="open", labels=("bug", "release:1.9.0")):
            return {"number": number, "title": title, "state": state,
                    "labels": [{"name": label} for label in labels]}
        rows = [item(8), item(2), item(1, state="closed"), item(3, title="BUG-19051: owned"),
                item(4, labels=("bug", "enhancement", "release:1.9.0")),
                item(5, labels=("bug", "release:1.9.x")), item(6, labels=("question",))]
        with patch.object(triage, "repo_root", return_value=Path("fixture")), \
                patch.object(triage, "github_repo", return_value="owner/repo"), \
                patch.object(triage, "fetch_remote_work_items", return_value=rows) as fetch, \
                patch.object(triage, "run_triage") as edit, contextlib.redirect_stdout(io.StringIO()):
            triage.reconcile_pending(limit=1)
            edit.assert_called_once_with(2)
            edit.reset_mock()
            fetch.side_effect = TrackingError("incomplete remote scan")
            with self.assertRaises(TrackingError):
                triage.reconcile_pending()
            edit.assert_not_called()

    def test_pull_request_policy_job_has_no_issue_write_token(self):
        workflow = (Path(__file__).resolve().parents[2] /
                    ".github/workflows/issue-auto-triage.yml").read_text()
        self.assertIn("\npermissions: {}\n", workflow.split("\njobs:", 1)[0])
        policy = workflow.split("  policy-tests:\n", 1)[1].split("  triage:\n", 1)[0]
        self.assertIn("    permissions:\n      contents: read\n", policy)
        self.assertNotIn("issues: write", policy)
        self.assertNotIn("pull-requests: read", policy)
        triage = workflow.split("  triage:\n", 1)[1]
        self.assertIn("    if: github.event_name != 'pull_request'", triage)
        self.assertIn("    permissions:\n      contents: read\n      issues: write\n      pull-requests: read\n", triage)

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
        self.assertEqual(release_family({"release:1.9.x", "release:1.9.0"}), (1, 9))
        self.assertIsNone(release_family({"release:1.8.x", "release:1.9.0"}))
        self.assertIsNone(release_family({"release:1.9.0", "release:1.9.1"}))
        self.assertIsNone(release_family(set()))

    def test_title_id_and_form_prefix(self):
        self.assertEqual(owning_id("BUG-19047: startup failure"), "BUG-19047")
        self.assertEqual(owning_id("VS-1999: feature"), "VS-1999")
        self.assertIsNone(owning_id("Compare with BUG-19047"))
        self.assertEqual(clean_title("[Bug]: File left behind"), "File left behind")

    def test_form_titles_resolve_explicit_user_choice(self):
        self.assertEqual(classify_form_title("[Bug]: path error"), "bug")
        self.assertEqual(classify_form_title("[Crash]: startup"), "bug")
        self.assertEqual(classify_form_title("[Feature]: New idea"), "feature")
        self.assertIsNone(classify_form_title("New idea maybe bug"))

    def test_preserve_existing_assignee(self):
        self.assertEqual(required_assignee([], "ATAC-Helicopter"), "ATAC-Helicopter")
        self.assertIsNone(required_assignee([{"login": "ameenalqershi"}], "ATAC-Helicopter"))

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
