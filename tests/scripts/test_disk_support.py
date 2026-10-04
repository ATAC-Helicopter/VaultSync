import copy
import hashlib
import importlib.util
import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('disk_support', ROOT / 'scripts/disk_support.py')
disk = importlib.util.module_from_spec(spec)
spec.loader.exec_module(disk)
NOW = datetime(2026, 10, 4, 16, tzinfo=timezone.utc)


class DiskSupportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        procedure = self.root / 'docs/adr/001-imaging-engine-1.9.md'
        procedure.parent.mkdir(parents=True)
        procedure.write_text('Unit-test procedure fixture. No device operation.')
        self.data = json.loads((ROOT / 'release/disk-support-1.9.0.json').read_text())
        self.data['profiles'] = self.data['profiles'][:1]

    def write_artifact(self, name, report):
        content = json.dumps(report).encode()
        (self.root / name).write_bytes(content)
        return {'path': name, 'sha256': hashlib.sha256(content).hexdigest()}

    def measured_fixture(self):
        # These synthetic unit-test reports are never added to the release matrix.
        self.data['engine'].update(decision='approved', approval={
            'by': 'test reviewer', 'observedAt': '2026-10-04T10:00:00Z',
            'artifact': self.write_artifact('approval.json', {'fixture': True}),
        })
        profile = self.data['profiles'][0]
        profile.update(status='qualified', environment='synthetic-test-only', hardwareKind='virtual')
        for n, stage in enumerate(disk.STAGES):
            report = dict(stage=stage, profileId=profile['id'], kind='measured', outcome='passed',
                          environment=profile['environment'], hardwareKind='virtual',
                          runId='fixture-run', imageRoot='a' * 64, captureBuild='fixture-build',
                          recoveryBuild='fixture-recovery', observedAt=f'2026-10-04T11:0{n}:00Z',
                          checks={check: 'passed' for check in disk.STAGE_CHECKS[stage]})
            profile['evidence'].append(self.write_artifact(f'{stage}.json', report))
        return profile

    def alter_report(self, profile, index, **changes):
        reference = profile['evidence'][index]
        report = json.loads((self.root / reference['path']).read_text())
        report.update(changes)
        profile['evidence'][index] = self.write_artifact(reference['path'], report)

    def check(self, require=False):
        return disk.validate(self.root, self.data, require, NOW)

    def test_candidate_plan_is_valid_but_does_not_pass_stable_gate(self):
        self.assertEqual([], self.check())
        self.assertTrue(self.check(require=True))
        self.assertEqual([], disk.validate(ROOT, json.loads((ROOT / 'release/disk-support-1.9.0.json').read_text())))

    def test_complete_bound_fixture_passes_structural_gate(self):
        self.measured_fixture()
        self.assertEqual([], self.check(require=True))

    def test_unapproved_engine_and_incomplete_loop_cannot_qualify(self):
        profile = self.measured_fixture()
        approved = copy.deepcopy(self.data['engine'])
        self.data['engine'] = dict(proposal='internal-raw-offline', decision='proposed', approval=None)
        self.assertTrue(self.check())
        self.data['engine'] = approved
        profile['evidence'].pop()
        self.assertTrue(self.check())

    def test_simulation_confirmation_failure_and_missing_scope_are_rejected(self):
        profile = self.measured_fixture()
        original = copy.deepcopy(profile)
        for changes in ({'kind': 'simulation'}, {'kind': 'user-confirmation'}, {'outcome': 'interrupted'},
                        {'checks': {}}, {'checks': {'target-byte-coverage': 'passed'}}):
            with self.subTest(changes=changes):
                profile.update(copy.deepcopy(original))
                self.alter_report(profile, 4, **changes)
                self.assertTrue(self.check())

    def test_run_image_build_profile_stage_and_environment_drift_are_rejected(self):
        profile = self.measured_fixture()
        original = copy.deepcopy(profile)
        for changes in ({'runId': 'other'}, {'imageRoot': 'b' * 64}, {'captureBuild': 'other'},
                        {'profileId': 'other'}, {'stage': 'capture'}, {'environment': 'other'},
                        {'hardwareKind': 'physical'}):
            with self.subTest(changes=changes):
                profile.update(copy.deepcopy(original))
                self.alter_report(profile, 4, **changes)
                self.assertTrue(self.check())

    def test_future_invalid_and_out_of_order_observations_are_rejected(self):
        profile = self.measured_fixture()
        original = copy.deepcopy(profile)
        for value in ('2027-01-01T00:00:00Z', '2026-10-04', '2026-10-04Z',
                      '2026-10-04T11:04:00+02:00', '2026-10-04T10:00:00Z'):
            with self.subTest(value=value):
                profile.update(copy.deepcopy(original))
                self.alter_report(profile, 4, observedAt=value)
                self.assertTrue(self.check())

    def test_missing_modified_and_escaping_artifacts_are_rejected(self):
        profile = self.measured_fixture()
        original = copy.deepcopy(profile['evidence'][4])
        for path in ('/etc/passwd', '../escape.json', 'missing.json'):
            with self.subTest(path=path):
                profile['evidence'][4] = {**original, 'path': path}
                self.assertTrue(self.check())
        profile['evidence'][4] = original
        (self.root / original['path']).write_text('{}')
        self.assertTrue(self.check())
        (self.root / 'escape.json').symlink_to(ROOT / 'LICENSE')
        profile['evidence'][4] = {**original, 'path': 'escape.json'}
        self.assertTrue(self.check())

    def test_unsupported_scope_duplicate_ids_and_wrong_types_are_rejected(self):
        original = copy.deepcopy(self.data)
        for changes in ({'liveCapture': True}, {'secureBoot': True}, {'encryption': 'luks'},
                        {'logicalSectorBytes': True}, {'sourceOS': 'macos'}, {'status': 'supported'},
                        {'owner': ''}, {'evidence': [{'outcome': 'passed'}]}):
            with self.subTest(changes=changes):
                self.data = copy.deepcopy(original)
                self.data['profiles'][0].update(changes)
                self.assertTrue(self.check())
        self.data = copy.deepcopy(original)
        self.data['profiles'].append(copy.deepcopy(self.data['profiles'][0]))
        self.assertTrue(self.check())
        for data in (None, [], {'schemaVersion': 99}, {**original, 'profiles': [None]},
                     {**original, 'exclusions': []}):
            with self.subTest(data=data):
                self.assertTrue(disk.validate(self.root, data))

    def test_approval_record_must_exist_and_match_its_hash(self):
        self.measured_fixture()
        approval = self.data['engine']['approval']
        approval['artifact']['sha256'] = '0' * 64
        self.assertTrue(self.check())
        approval['by'] = ''
        self.assertTrue(self.check())
