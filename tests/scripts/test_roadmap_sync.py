import argparse
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SCRIPT_PATH = ROOT / "scripts" / "roadmap_sync.py"
FIXTURE_ROOT = ROOT / "tests" / "scripts" / "fixtures" / "roadmap_sync"
SPEC = importlib.util.spec_from_file_location("roadmap_sync", SCRIPT_PATH)
roadmap_sync = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = roadmap_sync
SPEC.loader.exec_module(roadmap_sync)


class RoadmapSyncTests(unittest.TestCase):
    def test_parser_reconstructs_wrapped_titles_and_nested_contracts(self):
        index = roadmap_sync.parse_roadmap(
            (FIXTURE_ROOT / "roadmap.md").read_text(encoding="utf-8")
        )

        wrapped = index["VS-2001"]
        self.assertEqual(
            "VS-2001: Preserve a complete wrapped ticket title across every physical line in the roadmap",
            wrapped.title,
        )
        self.assertIn("- Scope:\n  - Keep nested scope details attached.", wrapped.description)
        self.assertIn("- Acceptance:", wrapped.description)
        self.assertTrue(index["BUG-2002"].completed)
        self.assertEqual("REL-2003: Consecutive ticket parsing", index["REL-2003"].title)

    def test_parser_ignores_invalid_tickets_and_handles_numbered_contract_items(self):
        index = roadmap_sync.parse_roadmap(
            """
# Safety
- [!] BUG-1 Invalid marker
- [ ] NOPE-2 Invalid identifier
- [ ] BUG-3 P1 Valid title
  1. First acceptance criterion
  continuation after a blank line

  - Scope item
"""
        )

        self.assertEqual(["BUG-3"], list(index))
        self.assertIn("1. First acceptance criterion", index["BUG-3"].description)
        self.assertIn("- Scope item", index["BUG-3"].description)

    def test_planner_updates_managed_bodies_and_preserves_manual_contracts(self):
        index = roadmap_sync.parse_roadmap(
            (FIXTURE_ROOT / "roadmap.md").read_text(encoding="utf-8")
        )
        items = json.loads((FIXTURE_ROOT / "items.json").read_text(encoding="utf-8"))["items"]

        changes = roadmap_sync.plan_changes(items, index)

        managed = next(change for change in changes if change.issue_number == 2001)
        self.assertFalse(managed.title_changed)
        self.assertEqual("update", managed.body_action)
        self.assertIn("Keep nested scope details attached", managed.new_body)
        manual_changes = [change for change in changes if change.issue_number == 2002]
        self.assertEqual([], manual_changes)
        draft = next(change for change in changes if change.content_type == "DraftIssue")
        self.assertEqual("update", draft.body_action)
        self.assertIn("Non-goal", draft.new_body)

    def test_planner_never_replaces_a_longer_managed_contract_with_a_fragment(self):
        index = roadmap_sync.parse_roadmap(
            (FIXTURE_ROOT / "roadmap.md").read_text(encoding="utf-8")
        )
        item = {
            "id": "item-complete",
            "title": "VS-2001: Existing concise issue title",
            "content": {
                "type": "Issue",
                "number": 2001,
                "body": roadmap_sync.MANAGED_BODY_PREFIX + "\n" + ("complete contract\n" * 100),
            },
        }

        self.assertEqual([], roadmap_sync.plan_changes([item], index))

    def test_planner_skips_nonissues_unknown_tickets_and_unchanged_managed_bodies(self):
        entry = roadmap_sync.RoadmapEntry("BUG-1", "BUG-1: Fix", "Fixes", "Details", False)
        managed_item = {
            "id": "PVTI_item",
            "title": "BUG-1: Fix",
            "status": "Todo",
            "priority": "N/A",
            "release": "1.9.x",
            "area": "Core",
            "content": {"type": "Issue", "number": 1, "body": ""},
        }
        managed_item["content"]["body"] = roadmap_sync.build_managed_body(entry, managed_item)
        items = [
            {"title": "BUG-1", "content": {"type": "PullRequest"}},
            {"title": "No ticket", "content": {"type": "Issue"}},
            {"title": "BUG-999", "content": {"type": "Issue"}},
            managed_item,
        ]

        self.assertEqual([], roadmap_sync.plan_changes(items, {"BUG-1": entry}))

    def test_input_validation_rejects_unsafe_identifiers_and_external_paths(self):
        self.assertEqual("ATAC-Helicopter", roadmap_sync.validate_owner("ATAC-Helicopter"))
        self.assertEqual("7", roadmap_sync.validate_project_number(7))
        with self.assertRaises(ValueError):
            roadmap_sync.validate_owner("--help")
        with self.assertRaises(ValueError):
            roadmap_sync.validate_project_number(0)
        with tempfile.NamedTemporaryFile() as external:
            with self.assertRaises(ValueError):
                roadmap_sync.resolve_input_path(external.name)

    def test_snapshot_loading_is_dry_run_only(self):
        args = argparse.Namespace(
            items_snapshot_path=str(FIXTURE_ROOT / "items.json"),
            dry_run=False,
        )
        with self.assertRaises(ValueError):
            roadmap_sync.load_items(args)

        args.dry_run = True
        project_id, items = roadmap_sync.load_items(args)
        self.assertEqual("snapshot", project_id)
        self.assertEqual(3, len(items))

    @mock.patch.object(roadmap_sync, "run_gh")
    def test_live_loading_validates_github_results(self, run_gh):
        run_gh.side_effect = [
            json.dumps({"id": "PVT_project"}),
            json.dumps({"items": [{"id": "PVTI_item"}]}),
        ]
        args = argparse.Namespace(
            items_snapshot_path="",
            dry_run=True,
            owner="ATAC-Helicopter",
            project_number=7,
        )

        project_id, items = roadmap_sync.load_items(args)

        self.assertEqual("PVT_project", project_id)
        self.assertEqual([{"id": "PVTI_item"}], items)
        self.assertEqual(2, run_gh.call_count)

    @mock.patch.object(roadmap_sync, "run_gh")
    def test_apply_change_uses_structured_issue_and_draft_arguments(self, run_gh):
        issue = roadmap_sync.PlannedChange(
            "PVTI_issue", "Issue", 42, "old", "old", False, "update", "", "body"
        )
        draft = roadmap_sync.PlannedChange(
            "PVTI_draft", "DraftIssue", None, "old", "old", False, "update", "", "body"
        )

        roadmap_sync.apply_change(issue, "ATAC-Helicopter", "PVT_project")
        roadmap_sync.apply_change(draft, "ATAC-Helicopter", "PVT_project")

        self.assertEqual(2, run_gh.call_count)
        self.assertIn("ATAC-Helicopter/VaultSync", run_gh.call_args_list[0].args[0])
        self.assertIn("PVTI_draft", run_gh.call_args_list[1].args[0])

    def test_apply_change_rejects_invalid_remote_identifiers(self):
        invalid_issue = roadmap_sync.PlannedChange(
            "", "Issue", -1, "", "", False, "update", "", "body"
        )
        invalid_draft = roadmap_sync.PlannedChange(
            "--help", "DraftIssue", None, "", "", False, "update", "", "body"
        )
        with self.assertRaises(ValueError):
            roadmap_sync.apply_change(invalid_issue, "ATAC-Helicopter", "PVT_project")
        with self.assertRaises(ValueError):
            roadmap_sync.apply_change(invalid_draft, "ATAC-Helicopter", "PVT_project")

    def test_offline_dry_run_reports_exact_changes_without_writing(self):
        completed = subprocess.run(
            [
                sys.executable,
                str(SCRIPT_PATH),
                "--roadmap-path",
                str(FIXTURE_ROOT / "roadmap.md"),
                "--items-snapshot-path",
                str(FIXTURE_ROOT / "items.json"),
                "--dry-run",
            ],
            check=True,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )

        report = json.loads(completed.stdout)
        self.assertTrue(report["dryRun"])
        self.assertEqual(3, report["indexed"])
        self.assertEqual(2, len(report["changes"]))
        self.assertEqual({"Issue", "DraftIssue"}, {item["content_type"] for item in report["changes"]})


if __name__ == "__main__":
    unittest.main()
