"""The benchmark's comparison frames must preserve data and their metadata overhead."""
import sys
import unittest
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from experiments.compression import benchmark


class BenchmarkControlTests(unittest.TestCase):
    def test_native_comparison_wrappers_preserve_empty_binary_numeric_and_guid_bytes(self):
        reference = benchmark.hex_reference(lambda b: zlib.compress(b, 3), zlib.decompress)
        pairs = ((benchmark.structure_zlib, benchmark.unstructure_zlib),
                 (benchmark.hex_zlib, benchmark.unhex_zlib), reference)
        samples = [b'', bytes(range(256)),
                   b'{"x":123456789,"y":123456790}\n' * 200,
                   b'guid: abcdef0123456789abcdef0123456789\n' * 200]
        for encode, decode in pairs:
            for data in samples:
                with self.subTest(codec=encode.__name__, size=len(data)):
                    frame = encode(data)
                    self.assertGreaterEqual(len(frame), 2)
                    self.assertIn(frame[0], (0, 1))
                    self.assertEqual(decode(frame), data)
