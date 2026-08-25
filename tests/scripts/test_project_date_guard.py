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
    def test_normalize_items_preserves_content_and_supported_field_values(self):
        pages = [
            {
                "data": {
                    "user": {
                        "projectV2": {
                            "items": {
                                "nodes": [
                                    {
                                        "id": "PVTI_item",
                                        "content": {"title": "Dated work"},
                                        "fieldValues": {
                                            "nodes": [
                                                {
                                                    "date": "2026-08-25",
                                                    "field": {"name": "Start date"},
                                                },
                                                {
                                                    "name": "In progress",
                                                    "field": {"name": "Status"},
                                                },
                                                {
                                                    "date": None,
                                                    "field": {"name": "Ignored"},
                                                },
                                            ]
                                        },
                                    },
                                    {
                                        "id": "PVTI_draft",
                                        "content": None,
                                    },
                                ]
                            }
                        }
                    }
                }
            }
        ]

        normalized = project_date_guard.normalize_items(pages)

        self.assertEqual(
            {
                "id": "PVTI_item",
                "content": {"title": "Dated work"},
                "fields": {"Start date": "2026-08-25", "Status": "In progress"},
            },
            normalized[0],
        )
        self.assertEqual(
            {"id": "PVTI_draft", "content": {}, "fields": {}},
            normalized[1],
        )

    def test_normalize_repository_dates_uses_published_dates_and_ignores_incomplete_nodes(self):
        response = {
            "data": {
                "repository": {
                    "milestones": {
                        "nodes": [
                            {"title": "1.8.8", "dueOn": "2026-08-28T00:00:00Z"},
                            {"title": "No date", "dueOn": None},
                        ]
                    },
                    "releases": {
                        "nodes": [
                            {"tagName": "v1.8.7", "publishedAt": "2026-08-21T09:00:00Z"},
                            {"tagName": "", "publishedAt": "2026-08-20T09:00:00Z"},
                        ]
                    },
                }
            }
        }

        milestones, releases = project_date_guard.normalize_repository_dates(response)

        self.assertEqual({"1.8.8": "2026-08-28"}, milestones)
        self.assertEqual({"1.8.7": "2026-08-21"}, releases)

    @mock.patch.object(project_date_guard, "run_gh")
    def test_load_live_state_validates_and_normalizes_every_github_response(self, run_gh):
        run_gh.side_effect = [
            '{"id":"PVT_project"}',
            '{"fields":['
            '{"name":"Start date","id":"PVTF_start"},'
            '{"name":"Target date","id":"PVTF_target"},'
            '{"name":"Completed on","id":"PVTF_completed"}'
            ']}',
            '[{"data":{"user":{"projectV2":{"items":{"nodes":['
            '{"id":"PVTI_item","content":{"title":"Work"},"fieldValues":{"nodes":[]}}'
            ']}}}}}]',
            '{"data":{"repository":{'
            '"milestones":{"nodes":[{"title":"1.8.8","dueOn":"2026-08-28T00:00:00Z"}]},'
            '"releases":{"nodes":[{"tagName":"v1.8.7","publishedAt":"2026-08-21T00:00:00Z"}]}'
            '}}}',
        ]

        project_id, field_ids, items, milestones, releases = (
            project_date_guard.load_live_state("ATAC-Helicopter", 7)
        )

        self.assertEqual("PVT_project", project_id)
        self.assertEqual(
            {
                "Start date": "PVTF_start",
                "Target date": "PVTF_target",
                "Completed on": "PVTF_completed",
            },
            field_ids,
        )
        self.assertEqual("PVTI_item", items[0]["id"])
        self.assertEqual({"1.8.8": "2026-08-28"}, milestones)
        self.assertEqual({"1.8.7": "2026-08-21"}, releases)
        self.assertEqual(4, run_gh.call_count)

    @mock.patch.object(project_date_guard, "run_gh")
    def test_load_live_state_fails_closed_when_required_project_fields_are_missing(self, run_gh):
        run_gh.side_effect = [
            '{"id":"PVT_project"}',
            '{"fields":[{"name":"Start date","id":"PVTF_start"}]}',
        ]

        with self.assertRaisesRegex(ValueError, "missing required date fields"):
            project_date_guard.load_live_state("ATAC-Helicopter", 7)

        self.assertEqual(2, run_gh.call_count)

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
