"""Exercise issue creation without granting tests any real GitHub/Git mutations."""
import argparse
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('work_tracking_boundaries', ROOT / 'scripts/work_tracking.py')
tracking = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = tracking
spec.loader.exec_module(tracking)


class TrackingBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / '.github').mkdir()
        (self.root / 'release').mkdir()
        self.config = json.loads((ROOT / '.github/work-tracking.json').read_text())
        (self.root / '.github/work-tracking.json').write_text(json.dumps(self.config))
        (self.root / 'release/release-metadata.json').write_text('{"activeRelease":{"version":"1.9.0"}}')
        self.calls = []
        self.remote_failure = False

    def execute(self, args, **kwargs):
        self.calls.append((args, kwargs))
        result = tracking.CommandResult
        if args[:3] == ['git', 'branch', '--show-current']:
            return result('work/fixture', '', 0)
        if args[:4] == ['git', 'config', '--get-all', 'vaultsync.reservedWorkId']:
            return result('', '', 1)
        if args[:2] == ['git', 'config']:
            return result('', '', 0)
        if args[:3] == ['gh', 'repo', 'view']:
            return result('owner/repo', '', 0)
        if args[:2] == ['gh', 'api']:
            collection = 'pullRequests' if 'pullRequests(' in kwargs.get('input_text', '') else 'issues'
            payload = {'data': {'repository': {collection: {'totalCount':0, 'nodes':[], 'pageInfo':{'hasNextPage':False, 'endCursor':None}}}}}
            return result('', 'API forbidden', 1) if self.remote_failure else result(json.dumps(payload), '', 0)
        if args[:3] == ['gh', 'label', 'list']:
            labels = ['kind:bug', 'bug', 'priority:p1', 'priority:P1', 'release:1.9.x']
            return result(json.dumps([{'name':name} for name in labels]), '', 0)
        if args[:3] == ['gh', 'issue', 'create']:
            return result('https://github.com/owner/repo/issues/42', '', 0)
        if args[:3] == ['gh', 'issue', 'view']:
            return result(json.dumps({'number':42, 'title':'Synthetic fixture', 'url':'https://github.com/owner/repo/issues/42'}), '', 0)
        if args[:3] == ['gh', 'project', 'item-add']:
            return result('', '', 0)
        raise AssertionError('Unexpected external command: ' + repr(args))

    def arguments(self):
        return argparse.Namespace(type='bug', title='Literal $(never-execute) `text`', release=None,
                                  priority='P1', area='Security', branch='current', context='Boundary fixture')

    def test_current_branch_issue_creation_preserves_literal_input_and_records_owner(self):
        with patch.object(tracking, 'repo_root', return_value=self.root), patch.object(tracking, 'run', side_effect=self.execute), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(tracking.cmd_issue(self.arguments()), 0)
        create = next(c for c in self.calls if c[0][:3] == ['gh', 'issue', 'create'])
        self.assertIn('Literal $(never-execute) `text`', create[0][create[0].index('--title') + 1])
        self.assertIn('--body-file', create[0])
        self.assertIn('Boundary fixture', create[1]['input_text'])
        self.assertTrue(any(c[0][:4] == ['git','config','--add','vaultsync.reservedWorkId'] for c in self.calls))
        self.assertTrue(any('branch.work/fixture.fgIssue' in c[0] for c in self.calls))
        self.assertFalse(any(c[0][:2] == ['git','switch'] for c in self.calls))

    def test_incomplete_remote_audit_cannot_allocate_create_or_associate_an_issue(self):
        self.remote_failure = True
        with patch.object(tracking, 'repo_root', return_value=self.root), patch.object(tracking, 'run', side_effect=self.execute):
            with self.assertRaises(tracking.TrackingError):
                tracking.cmd_issue(self.arguments())
        self.assertFalse(any(c[0][:3] == ['gh','issue','create'] for c in self.calls))
        self.assertFalse(any(c[0][:2] == ['git','config'] and '--get-all' not in c[0] for c in self.calls))

    def test_missing_or_invalid_configuration_and_detached_head_fail_closed(self):
        config = self.root / '.github/work-tracking.json'
        config.unlink()
        with self.assertRaises(tracking.TrackingError): tracking.load_config(self.root)
        config.write_text('not-json')
        with self.assertRaises(tracking.TrackingError): tracking.load_config(self.root)
        with patch.object(tracking, 'run', return_value=tracking.CommandResult('', '', 0)):
            with self.assertRaises(tracking.TrackingError): tracking.current_branch(self.root)

    def test_branch_collision_is_refused_without_switching_or_overwriting(self):
        responses = [tracking.CommandResult('work/fixture', '', 0), tracking.CommandResult('', '', 0)]
        with patch.object(tracking, 'run', side_effect=responses) as execute:
            with self.assertRaises(tracking.TrackingError):
                tracking.maybe_create_branch(self.root, mode='new', work_id='BUG-19034', title='Fixture', branch_prefix='fix')
            self.assertEqual(execute.call_count, 2)
            self.assertNotIn('switch', execute.call_args.args[0])
