"""Offline tests for safe Project date policy."""
import sys
import unittest
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from project_date_sync import desired_date_changes


class ProjectDateSyncTests(unittest.TestCase):
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
