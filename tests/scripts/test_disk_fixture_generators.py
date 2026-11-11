"""Regeneration must match the independent, reviewed reader fixtures byte for byte."""
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
import generate_gpt_fixtures
import generate_origin_fixtures


class DiskFixtureGeneratorTests(unittest.TestCase):
    def test_gpt_headers_tables_and_adversarial_collision_remain_reproducible(self):
        expected = json.loads((ROOT / 'tests/VaultSync.Core.Tests/Fixtures/disk-gpt-v1.json').read_text())
        self.assertEqual([generate_gpt_fixtures.generate(sector) for sector in (512, 4096)], expected)

    def test_origin_manifest_completion_and_source_identity_match_reviewed_fixtures(self):
        directory = ROOT / 'tests/VaultSync.Core.Tests/Fixtures'
        gpt = json.loads((directory / 'disk-gpt-v1.json').read_text())
        expected = json.loads((directory / 'disk-image-origin-v2.json').read_text())
        self.assertEqual([generate_origin_fixtures.generate(fixture) for fixture in gpt], expected)
