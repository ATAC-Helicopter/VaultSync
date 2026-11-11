import os
from pathlib import Path
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
from release_source import PREPARATION_REF, beta_source_allowed


class BetaSourceTests(unittest.TestCase):
    def test_preparation_is_scoped_to_one_release_and_numeric_beta_sequence(self):
        for version in ('1.9.0-beta.1', '1.9.0-beta.2', '1.9.0-beta.998'):
            self.assertTrue(beta_source_allowed(version, PREPARATION_REF))
        for version in ('1.9.0', '1.9.1-beta.1', '1.10.0-beta.1', '1.9.0-rc.1',
                        '1.9.0-beta.0', '1.9.0-beta.01', '1.9.0-beta.999',
                        '1.9.0-beta.1+local', 'v1.9.0-beta.1', '1.9.0-beta.1\n'):
            with self.subTest(version=version):
                self.assertFalse(beta_source_allowed(version, PREPARATION_REF))

    def test_dev_compatibility_and_foreign_sources(self):
        self.assertTrue(beta_source_allowed('1.8.9-Beta.1', 'refs/heads/Dev'))
        self.assertFalse(beta_source_allowed('1.9.0', 'refs/heads/Dev'))
        for ref in ('refs/heads/Stable', 'refs/heads/work/other', PREPARATION_REF + '/extra',
                    'refs/tags/v1.9.0-beta.1', PREPARATION_REF + '\n'):
            self.assertFalse(beta_source_allowed('1.9.0-beta.1', ref))

    def test_workflow_executes_the_shared_policy_before_qualified_profile_gate(self):
        workflow = (ROOT / '.github/workflows/release-assets.yml').read_text()
        start = workflow.index('          if [[ "$channel" == "beta" ]]; then')
        end = workflow.index('          if [[ "$channel" == "stable" ]]; then', start)
        policy = workflow[start:end]
        self.assertLess(policy.index('scripts/release_source.py'), policy.index('--require-qualified'))
        # Execute the actual workflow beta block with the expensive downstream disk
        # gate removed, rather than testing a duplicate shell authorization policy.
        policy = policy.replace('python3 scripts/disk_support.py check --require-qualified', ':')
        for ref, version, candidate, accepted in (
            (PREPARATION_REF, '1.9.0-beta.1', 'false', True),
            ('refs/heads/Dev', '1.9.0-beta.1', 'false', True),
            (PREPARATION_REF, '1.9.1-beta.1', 'false', False),
            ('refs/heads/work/other', '1.9.0-beta.1', 'false', False),
            (PREPARATION_REF, '1.9.0-beta.1', 'true', False),
            (PREPARATION_REF, '1.9.0', 'false', False),
        ):
            with self.subTest(ref=ref, version=version, candidate=candidate):
                result = subprocess.run(['bash', '-c', 'set -euo pipefail\n' + policy], cwd=ROOT,
                    env={**os.environ, 'channel': 'beta', 'target': version,
                         'release_candidate': candidate, 'GITHUB_REF': ref},
                    capture_output=True, text=True)
                self.assertEqual(accepted, result.returncode == 0, result.stderr)
