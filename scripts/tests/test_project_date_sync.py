"""Offline tests for safe Project date policy."""
import sys
import copy
import contextlib
import io
import unittest
from datetime import date, datetime, timezone
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from project_date_sync import desired_date_changes
import project_date_sync as sync


class ProjectDateSyncTests(unittest.TestCase):
    def test_complete_project_scan_and_incomplete_pages_fail_closed(self):
        def page(nodes, more=False, cursor=None, total=2):
            return {"user": {"projectV2": {"id": "project", "fields": {
                "pageInfo": {"hasNextPage": False}, "nodes": [
                    {"id": "start", "name": "Start date"},
                    {"id": "completed", "name": "Completed on"}]},
                "items": {"totalCount": total, "nodes": nodes,
                          "pageInfo": {"hasNextPage": more, "endCursor": cursor}}}}}
        first, last = page([{"id": "one"}], True, "next"), page([{"id": "two"}])
        with patch.object(sync, "graphql", side_effect=[first, last]) as api:
            self.assertEqual(sync.load_project(), ("project", {
                "Start date": "start", "Completed on": "completed"},
                [{"id": "one"}, {"id": "two"}]))
            self.assertEqual(api.call_args_list[1].args[1]["cursor"], "next")
        partial_fields = copy.deepcopy(first)
        partial_fields["user"]["projectV2"]["fields"]["pageInfo"]["hasNextPage"] = True
        for pages in ([{"user": None}], [partial_fields],
                      [first, page([], total=3)], [page([])],
                      [first, page([], True, "next")]):
            with self.subTest(pages=pages), patch.object(sync, "graphql", side_effect=pages):
                with self.assertRaises(sync.TrackingError):
                    sync.load_project()

    def test_reconciliation_limits_writes_and_rejects_partial_batches(self):
        def item(number, status, completed=None, repository=sync.REPOSITORY):
            values = [{"field": {"name": "Status"}, "name": status}]
            if completed:
                values.append({"field": {"name": "Completed on"}, "date": completed})
            return {"id": str(number), "content": {"number": number,
                "createdAt": "2026-11-14T00:00:00Z", "closedAt": None,
                "repository": {"nameWithOwner": repository}},
                "fieldValues": {"pageInfo": {"hasNextPage": False}, "nodes": values}}
        fields = {"Start date": "start", "Completed on": "completed"}
        rows = [item(1, "Todo", "2026-11-15"), item(2, "In progress"),
                item(3, "Done"), item(4, "In progress", repository="other/repo"),
                {"content": None}, item(5, "Unknown")]
        with patch.dict(sync.os.environ, {"GH_TOKEN": "fixture-only"}), \
                patch.object(sync, "load_project", return_value=("project", fields, rows)), \
                patch.object(sync, "graphql") as api, patch.object(sync, "datetime") as clock, \
                contextlib.redirect_stdout(io.StringIO()):
            clock.now.return_value = datetime(2026, 11, 17, tzinfo=timezone.utc)
            sync.main()
            self.assertEqual(api.call_count, 2)
            self.assertEqual(api.call_args_list[0].args, (sync.MUTATE_CLEAR,
                {"p": "project", "i": "1", "f": "completed"}))
            self.assertEqual(api.call_args_list[1].args[1], {
                "p": "project", "i": "2", "f": "start", "d": "2026-11-17"})
            partial = item(6, "Todo")
            partial["fieldValues"]["pageInfo"]["hasNextPage"] = True
            for bad_rows in ([rows[0], partial], [item(n, "Todo", "2026-11-15") for n in range(101)]):
                api.reset_mock()
                with patch.object(sync, "load_project", return_value=("project", fields, bad_rows)):
                    with self.assertRaises(sync.TrackingError):
                        sync.main()
                api.assert_not_called()

    def test_new_in_progress_receives_first_observed_start(self):
        self.assertEqual(
            desired_date_changes("In progress", None, None,
                                 "2026-11-13T18:00:00Z", None, date(2026, 11, 14)),
            [("set", "Start date", "2026-11-14")],
        )

    def test_old_items_do_not_receive_invented_start_dates(self):
        self.assertEqual(
            desired_date_changes("In progress", None, None,
                                 "2026-09-01T12:00:00Z", None, date(2026, 11, 14)), [],
        )

    def test_closed_issue_uses_exact_closure_date(self):
        self.assertEqual(
            desired_date_changes("Done", "2026-11-13", None,
                                 "2026-11-13T18:00:00Z", "2026-11-14T00:02:00Z",
                                 date(2026, 11, 18)),
            [("set", "Completed on", "2026-11-14")],
        )

    def test_reopen_clears_completion(self):
        self.assertEqual(
            desired_date_changes("Todo", "2026-11-13", "2026-11-14",
                                 "2026-11-13T18:00:00Z", None, date(2026, 11, 18)),
            [("clear", "Completed on", None)],
        )

    def test_existing_dates_are_preserved(self):
        self.assertEqual(
            desired_date_changes("In progress", "2026-10-12", None,
                                 "2026-11-13T18:00:00Z", None, date(2026, 11, 18)), [],
        )


if __name__ == "__main__":
    unittest.main()
