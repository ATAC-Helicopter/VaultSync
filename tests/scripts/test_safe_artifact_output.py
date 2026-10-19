import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
from safe_artifact_output import write_text_exclusive


class EvidenceOutputTests(unittest.TestCase):
    def test_new_output_is_private_utf8_and_never_replaces_prior_evidence(self):
        with tempfile.TemporaryDirectory() as root:
            target = Path(root) / 'new' / 'evidence.json'
            write_text_exclusive(target, 'original é\n')
            with self.assertRaises(FileExistsError):
                write_text_exclusive(target, 'replacement')
            self.assertEqual(target.read_text(), 'original é\n')
            if os.name != 'nt':
                self.assertEqual(target.stat().st_mode & 0o777, 0o600)

    def test_parent_traversal_and_ambiguous_names_fail_before_creating_directories(self):
        with tempfile.TemporaryDirectory() as root:
            for name in ('nested/../outside.json', 'nested/.private', 'nested/--argument', 'nested/bad\nname'):
                with self.subTest(name=name), self.assertRaises(ValueError):
                    write_text_exclusive(Path(root) / name, 'forbidden')
            self.assertFalse((Path(root) / 'nested').exists())

    @unittest.skipIf(os.name == 'nt', 'Symlink creation requires additional Windows privileges')
    def test_symlink_and_hardlink_targets_are_not_overwritten(self):
        with tempfile.TemporaryDirectory() as root:
            source = Path(root) / 'private.txt'
            source.write_text('private')
            for kind in ('symlink', 'hardlink'):
                target = Path(root) / (kind + '.json')
                if kind == 'symlink': target.symlink_to(source)
                else: os.link(source, target)
                with self.assertRaises(FileExistsError):
                    write_text_exclusive(target, 'replacement')
                self.assertEqual(source.read_text(), 'private')
