import importlib.util
import sys
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SCRIPT_PATH = ROOT / "scripts" / "project_date_guard.py"
SCRIPTS_ROOT = str(SCRIPT_PATH.parent)
if SCRIPTS_ROOT not in sys.path:
    sys.path.insert(0, SCRIPTS_ROOT)
SPEC = importlib.util.spec_from_file_location("project_date_guard", SCRIPT_PATH)
project_date_guard = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = project_date_guard
SPEC.loader.exec_module(project_date_guard)


class ProjectDateGuardTests(unittest.TestCase):
    def test_planner_traces_creation_milestone_and_release_horizon_dates(self):
        items = [
            {
                "id": "PVTI_missing",
                "content": {
                    "title": "Planned issue",
                    "createdAt": "2026-07-27T22:43:39Z",
                    "milestone": {"title": "1.9.x", "dueOn": "2027-09-24T00:00:00Z"},
                },
                "fields": {"Status": "Todo", "Release": "1.9.x"},
            },
            {
                "id": "PVTI_candidate",
                "content": {
                    "title": "Candidate issue",
                    "createdAt": "2026-07-27T22:44:25Z",
                },
                "fields": {"Status": "Todo", "Release": "1.9.x"},
            },
        ]

        repairs, unresolved = project_date_guard.plan_repairs(
            items,
            {"1.9.x": "2027-09-24"},
            {},
        )

        self.assertEqual([], unresolved)
        values = {(repair.item_id, repair.field_name): repair.value for repair in repairs}
        self.assertEqual("2026-07-27", values[("PVTI_missing", "Start date")])
        self.assertEqual("2027-09-24", values[("PVTI_missing", "Target date")])
        self.assertEqual("2027-09-24", values[("PVTI_candidate", "Target date")])

    def test_planner_uses_published_release_when_old_milestone_has_no_due_date(self):
        item = {
            "id": "PVTI_historical",
            "content": {
                "title": "Historical fix",
                "createdAt": "2026-05-11T13:11:33Z",
                "closedAt": "2026-05-12T13:57:50Z",
                "milestone": {"title": "1.7.4", "dueOn": None},
            },
            "fields": {"Status": "Done", "Completed on": "2026-05-12"},
        }

        repairs, unresolved = project_date_guard.plan_repairs(
            [item], {}, {"1.7.4": "2026-05-20"}
        )

        self.assertEqual([], unresolved)
        self.assertEqual(
            {"Start date": "2026-05-11", "Target date": "2026-05-20"},
            {repair.field_name: repair.value for repair in repairs},
        )

    def test_planner_fails_closed_when_a_required_date_has_no_source(self):
        repairs, unresolved = project_date_guard.plan_repairs(
            [{"id": "PVTI_unknown", "content": {"title": "Unknown"}, "fields": {}}],
            {},
            {},
        )

        self.assertEqual([], repairs)
        self.assertEqual({"Start date", "Target date"}, {item.field_name for item in unresolved})

    @mock.patch.object(project_date_guard, "run_gh")
    def test_apply_uses_date_typed_project_updates(self, run_gh):
        repair = project_date_guard.DateRepair(
            "PVTI_item", "Issue", "Start date", "2026-08-24", "creation date"
        )

        project_date_guard.apply_repairs(
            [repair], "PVT_project", {"Start date": "PVTF_start"}
        )

        self.assertEqual(1, run_gh.call_count)
        arguments = run_gh.call_args.args[0]
        self.assertEqual("project", arguments[0])
        self.assertIn("--date", arguments)
        self.assertIn("2026-08-24", arguments)


if __name__ == "__main__":
    unittest.main()
