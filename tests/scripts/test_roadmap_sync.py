import importlib.util
import json
import subprocess
import sys
import unittest
from pathlib import Path


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
