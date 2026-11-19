import copy
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('release_family', ROOT / 'scripts/release_family.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ReleaseFamilyTests(unittest.TestCase):
    def setUp(self):
        self.data = json.loads((ROOT / 'release/1.9-family.json').read_text())

    def test_import_and_ownership_match_canonical_source(self):
        self.assertEqual([], module.validate(ROOT, self.data))

    def test_duplicate_owner_and_changed_import_are_rejected(self):
        broken = copy.deepcopy(self.data)
        broken['releases'][1]['workIds'].append(broken['releases'][0]['workIds'][0])
        self.assertTrue(any('duplicate' in e for e in module.validate(ROOT, broken)))
        broken = copy.deepcopy(self.data)
        broken['source']['sha256'] = '0' * 64
        self.assertTrue(any('digest' in e for e in module.validate(ROOT, broken)))

    def test_branch_identity_and_alias_collisions_are_rejected(self):
        broken = copy.deepcopy(self.data)
        broken['releases'][1]['branch'] = 'release/1.9.5'
        broken['documentAliases'][1]['documentId'] = broken['documentAliases'][0]['documentId']
        errors = module.validate(ROOT, broken)
        self.assertTrue(any('branch' in e for e in errors))
        self.assertTrue(any('one-to-one' in e for e in errors))


    def test_alias_cannot_reuse_the_same_live_issue(self):
        broken = copy.deepcopy(self.data)
        broken['documentAliases'][1]['issue'] = broken['documentAliases'][0]['issue']
        self.assertTrue(any('one-to-one' in e for e in module.validate(ROOT, broken)))

    def test_missing_owner_and_external_import_fail_closed(self):
        broken = copy.deepcopy(self.data)
        broken['releases'][0]['workIds'].pop()
        self.assertTrue(any('unowned' in e for e in module.validate(ROOT, broken)))
        broken = copy.deepcopy(self.data)
        broken['source']['path'] = '../outside-planning.md'
        self.assertTrue(any('repository file' in e for e in module.validate(ROOT, broken)))
