import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
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
        self.receipts = [Path(self.temp.name) / name for name in ('storage.json', 'admission.json')]
        self.reset()

    def reset(self):
        for receipt in self.receipts:
            receipt.write_text(json.dumps(dict.fromkeys(source.SOURCE_FIELDS, self.SHA)), encoding='utf-8')

    def check(self, expected=None):
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return source.main([self.SHA if expected is None else expected, *map(str, self.receipts)])

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

    def test_usage_requires_expected_sha_and_exactly_two_receipts(self):
        with contextlib.redirect_stderr(io.StringIO()):
            for args in ([], [self.SHA], [self.SHA, 'one.json'], [self.SHA, 'one.json', 'two.json', 'three.json']):
                self.assertEqual(2, source.main(args))


if __name__ == '__main__':
    unittest.main()
