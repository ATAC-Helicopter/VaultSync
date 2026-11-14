import argparse
import json
import io
import pathlib
import sys
import tempfile
import unittest
from contextlib import ExitStack, redirect_stdout
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / 'scripts'))
import work_tracking as tracking


class AdoptionPriorityTests(unittest.TestCase):
    def test_roadmap_priority_replaces_historical_default_conflict(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / 'ROADMAP.md').write_text('- [ ] `VS-1905` `P0` Safety.\n')
            self.assertEqual('P0', tracking.adoption_priority(root, 'VS-1905', ['priority:p0', 'priority:p2'], None))
            self.assertEqual('P1', tracking.adoption_priority(root, 'VS-1905', ['priority:p0'], 'p1'))

    def test_existing_priority_is_preserved_without_roadmap_entry(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            self.assertEqual('P0', tracking.adoption_priority(root, 'VS-1905', ['priority:p0', 'area:core'], None))
            self.assertEqual('P2', tracking.adoption_priority(root, 'VS-1905', ['area:core'], None))
            with self.assertRaises(tracking.TrackingError):
                tracking.adoption_priority(root, 'VS-1905', ['priority:p0', 'priority:p2'], None)

    def test_adopt_reconciles_priorities_in_one_edit_without_touching_other_labels(self):
        calls, associated = self.adopt()
        edits = [args for args in calls if args[:3] == ['gh', 'issue', 'edit']]
        self.assertEqual(1, len(edits))
        args = edits[0]
        self.assertEqual('priority:p2', args[args.index('--remove-label') + 1])
        self.assertIn('priority:p0', args)
        self.assertNotIn('area:core', args)
        self.assertTrue(associated)

    def test_failed_priority_edit_cannot_report_successful_adoption(self):
        with self.assertRaises(tracking.TrackingError):
            self.adopt(fail_edit=True)

    def adopt(self, fail_edit=False):
        with tempfile.TemporaryDirectory() as directory, ExitStack() as stack:
            root = pathlib.Path(directory)
            (root / 'ROADMAP.md').write_text('- [ ] `VS-1905` `P0` Safety.\n')
            issue = dict(number=493, title='VS-1905: Safety', body='', url='https://example.test/493',
                         labels=[dict(name=name) for name in ('priority:p0', 'priority:p2', 'area:core')])
            calls = []
            def execute(args, **kwargs):
                calls.append(args)
                if args[:3] == ['gh', 'issue', 'view']:
                    return tracking.CommandResult(json.dumps(issue), '', 0)
                if fail_edit:
                    raise tracking.TrackingError('permission denied')
                return tracking.CommandResult('', '', 0)
            values = dict(repo_root=root, load_config={'types': {'feature': {'branch_prefix': 'feat'}}},
                          current_branch='work/release', infer_release_family=(1, 9),
                          select_labels=['priority:p0', 'enhancement'], maybe_create_branch='work/release')
            for name, value in values.items():
                stack.enter_context(patch.object(tracking, name, return_value=value))
            stack.enter_context(patch.object(tracking, 'run', side_effect=execute))
            stack.enter_context(patch.object(tracking, 'add_to_project'))
            associated = stack.enter_context(patch.object(tracking, 'set_branch_tracking'))
            try:
                with redirect_stdout(io.StringIO()):
                    tracking.cmd_adopt(argparse.Namespace(issue=493, type='feature', release=None, priority=None, branch='current'))
            except tracking.TrackingError:
                self.assertFalse(associated.called)
                raise
            return calls, associated.called


if __name__ == '__main__':
    unittest.main()
