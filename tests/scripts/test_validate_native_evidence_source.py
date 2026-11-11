import contextlib
import importlib.util
import io
import json
import os
import tempfile
import unittest
from unittest import mock
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('native_evidence_source', ROOT / 'scripts/validate_native_evidence_source.py')
source = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(source)


class NativeEvidenceSourceTests(unittest.TestCase):
    SHA = '0123456789abcdef0123456789abcdef01234567'

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.env = mock.patch.dict(os.environ, {'RUNNER_TEMP': str(self.root)})
        self.env.start()
        self.addCleanup(self.env.stop)
        self.receipts = [self.root / directory / name for directory, name in source.RECEIPTS]
        for receipt in self.receipts:
            receipt.parent.mkdir()
        self.reset()

    def reset(self):
        for receipt in self.receipts:
            receipt.write_text(json.dumps(dict.fromkeys(source.SOURCE_FIELDS, self.SHA)), encoding='utf-8')

    def check(self, expected=None):
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return source.main([self.SHA if expected is None else expected])

    def test_matching_pair_passes_without_claiming_kernel_authority(self):
        self.assertEqual(0, self.check())
        # Other native evidence is deliberately outside this helper's authority.
        value = dict.fromkeys(source.SOURCE_FIELDS, self.SHA)
        value['readonlyExt4Root'] = False
        self.receipts[0].write_text(json.dumps(value), encoding='utf-8')
        self.assertEqual(0, self.check())

    def test_every_source_field_in_either_receipt_must_match(self):
        for receipt in self.receipts:
            for field in source.SOURCE_FIELDS:
                with self.subTest(receipt=receipt.name, field=field):
                    self.reset()
                    value = dict.fromkeys(source.SOURCE_FIELDS, self.SHA)
                    value[field] = 'f' * 40
                    receipt.write_text(json.dumps(value), encoding='utf-8')
                    self.assertEqual(1, self.check())

    def test_missing_or_wrongly_typed_fields_refuse(self):
        for receipt in self.receipts:
            for field in source.SOURCE_FIELDS:
                for replacement in ('missing', None, True, 123, [], {}):
                    with self.subTest(receipt=receipt.name, field=field, replacement=replacement):
                        self.reset()
                        value = dict.fromkeys(source.SOURCE_FIELDS, self.SHA)
                        if replacement == 'missing':
                            del value[field]
                        else:
                            value[field] = replacement
                        receipt.write_text(json.dumps(value), encoding='utf-8')
                        self.assertEqual(1, self.check())

    def test_duplicate_fields_even_when_identical_or_nested_refuse(self):
        valid = json.dumps(dict.fromkeys(source.SOURCE_FIELDS, self.SHA))
        for encoded in (valid[:-1] + ',"sourceCommit":"' + self.SHA + '"}',
                        valid[:-1] + ',"extra":{"duplicate":1,"duplicate":1}}'):
            self.receipts[0].write_text(encoded, encoding='utf-8')
            self.assertEqual(1, self.check())

    def test_invalid_json_encoding_constants_or_root_types_refuse(self):
        for encoded in (b'', b'{', b'{} trailing', b'\xff', b'null', b'[]', b'"text"', b'1',
                        b'{"x":NaN}', b'{"x":Infinity}', b'{"x":-Infinity}', b'[' * 1500):
            with self.subTest(encoded=encoded[:30]):
                self.receipts[0].write_bytes(encoded)
                self.assertEqual(1, self.check())

    def test_size_bound_is_inclusive_and_checked_before_parse(self):
        encoded = self.receipts[0].read_bytes()
        self.receipts[0].write_bytes(encoded + b' ' * (source.MAXIMUM_BYTES - len(encoded)))
        self.assertEqual(0, self.check())
        with self.receipts[0].open('ab') as stream:
            stream.write(b' ')
        self.assertEqual(1, self.check())

    def test_missing_receipt_refuses(self):
        for receipt in self.receipts:
            self.reset()
            receipt.unlink()
            self.assertEqual(1, self.check())

    def test_expected_sha_must_be_exactly_forty_lowercase_hex_characters(self):
        for expected in ('', self.SHA.upper(), 'g' * 40, self.SHA[:-1], self.SHA + '0', self.SHA + '\n'):
            with self.subTest(expected=expected):
                self.assertEqual(1, self.check(expected))

    def test_usage_rejects_all_caller_selected_file_paths(self):
        with contextlib.redirect_stderr(io.StringIO()):
            for args in ([], [self.SHA, 'one.json'], [self.SHA, 'one.json', 'two.json']):
                self.assertEqual(2, source.main(args))

    def test_missing_relative_or_traversing_runner_root_refuses(self):
        for root in ('', '.', 'relative', str(self.root / '..')):
            with self.subTest(root=root), mock.patch.dict(os.environ, {'RUNNER_TEMP': root}):
                self.assertEqual(1, self.check())

    @unittest.skipUnless(hasattr(os, 'symlink') and os.name != 'nt', 'requires local symlink support')
    def test_receipt_and_parent_symlinks_cannot_escape_runner_root(self):
        with tempfile.TemporaryDirectory() as outside:
            external = Path(outside) / self.receipts[0].name
            external.write_bytes(self.receipts[0].read_bytes())
            self.receipts[0].unlink()
            self.receipts[0].symlink_to(external)
            self.assertEqual(1, self.check())
            self.receipts[0].unlink()
            self.receipts[0].parent.rmdir()
            self.receipts[0].parent.symlink_to(outside, target_is_directory=True)
            self.assertEqual(1, self.check())

    @unittest.skipUnless(hasattr(os, 'symlink') and os.name != 'nt', 'requires local symlink support')
    def test_runner_root_symlink_refuses(self):
        alias = self.root / 'alias'
        alias.symlink_to(self.root, target_is_directory=True)
        with mock.patch.dict(os.environ, {'RUNNER_TEMP': str(alias)}):
            self.assertEqual(1, self.check())

    def test_replaced_receipt_is_rejected_after_open(self):
        original_open = source.os.open
        def replace_before_open(path, flags, *args, **kwargs):
            if Path(path) == self.receipts[0]:
                replacement = self.root / 'replacement.json'
                replacement.write_bytes(self.receipts[0].read_bytes())
                os.replace(replacement, self.receipts[0])
            return original_open(path, flags, *args, **kwargs)
        with mock.patch.object(source.os, 'open', side_effect=replace_before_open):
            self.assertEqual(1, self.check())

    @unittest.skipUnless(hasattr(os, 'link'), 'requires hardlink support')
    def test_hardlinked_receipt_refuses(self):
        os.link(self.receipts[0], self.root / 'alias.json')
        self.assertEqual(1, self.check())

    @unittest.skipUnless(hasattr(os, 'mkfifo'), 'requires local FIFO support')
    def test_non_regular_receipt_refuses_without_waiting_for_a_writer(self):
        self.receipts[0].unlink()
        os.mkfifo(self.receipts[0])
        self.assertEqual(1, self.check())


if __name__ == '__main__':
    unittest.main()
