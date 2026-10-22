import hashlib
import random
import struct
import unittest
from experiments.compression import codec, structure


def stream(logical, skeleton, lanes):
    return struct.pack('<4sIIB', b'VSL1', logical, len(skeleton), len(lanes)) + skeleton + b''.join(
        struct.pack('<I', len(lane)) + lane for lane in lanes)


def frame(mode, logical, payload):
    prefix = codec.PREFIX.pack(codec.MAGIC, mode, len(logical), len(payload), hashlib.sha256(logical).digest())
    return prefix + hashlib.sha256(prefix + payload).digest() + payload


class StructuredCompressionTests(unittest.TestCase):
    def test_independent_fixture_preserves_numeric_spelling_and_negative_delta(self):
        # Values 7 -> 2 in one lane: zigzag deltas 14 and 9; leading zeros retained.
        fixture = stream(10, b'x\0\1\3\r\nx\0\1\2\n', [b'\x0e\x09'])
        self.assertEqual(structure.unpack(fixture), b'x007\r\nx02\n')

    def test_spelling_binary_and_line_endings_roundtrip(self):
        values = [b'', b'\x00\xff001\r\n-0 +0002 1.2300e-05\r', bytes(range(256)),
                  b'a' * 131072, b'1' * 131072, b'0\0' * 65536,
                  b'999999999999999999\n000000000000000000\n']
        for data in values:
            with self.subTest(size=len(data)):
                for policy in (1, 4):
                    packed = structure.pack(data, min_digits=policy)
                    if packed is not None:
                        self.assertEqual(structure.unpack(packed), data)
                result = codec.encode_structured(data)
                self.assertEqual(codec.decode(result), data)
                self.assertLessEqual(len(result), len(codec.encode_entropy(data)))

    def test_context_limit_and_randomized_roundtrips(self):
        generator = random.Random(199615)
        values = [b''.join(bytes([65 + i // 26, 65 + i % 26]) + b':000123\n' for i in range(100))]
        values += [generator.randbytes(generator.randrange(5000)) for _ in range(30)]
        for data in values:
            packed = structure.pack(data)
            if packed is not None:
                self.assertLessEqual(packed[12], structure.MAX_LANES)
                self.assertEqual(structure.unpack(packed), data)
            self.assertEqual(codec.decode(codec.encode_structured(data)), data)

    def test_useful_case_selects_structure_and_is_deterministic(self):
        data = b''.join(f'position: {i}\nspeed: {i % 200}\n'.encode() for i in range(2500))
        encoded = codec.encode_structured(data)
        self.assertEqual(encoded[8], 3)
        self.assertEqual(encoded, codec.encode_structured(data))
        self.assertEqual(codec.decode(encoded), data)
        self.assertLess(len(encoded), len(codec.encode_entropy(data)))

    def test_bad_structure_with_valid_outer_hashes_is_rejected(self):
        bad = [stream(1, b'\0', []), stream(1, b'\0\1\1', []),
               stream(1, b'\0\1\0', [b'\0']), stream(1, bytes((0, 1, 19)), [b'\0']),
               stream(1, b'\0\1\1', [b'\1']), stream(1, b'\0\1\1', [b'\x14']),
               stream(1, b'\0\1\1', [b'\x80\0']), stream(1, b'\0\1\1', [b'\x80' * 10]),
               stream(1, b'\0\1\1', [b'\0\0']), stream(0, b'a', []),
               stream(0, b'\0\0', []), stream(0, b'', []) + b'extra',
               struct.pack('<4sIIB', b'VSL1', 0, 0, 33),
               struct.pack('<4sIIB', b'VSL1', 131073, 0, 0)]
        for payload in bad:
            with self.subTest(payload=payload):
                with self.assertRaises(ValueError):
                    structure.unpack(payload)
                with self.assertRaises(ValueError):
                    codec.decode(frame(3, b'x', codec.encode_entropy(payload)))

    def test_truncation_corruption_and_recursive_structure_are_rejected(self):
        data = b'id: 0001\n' * 500
        packed = structure.pack(data)
        for end in range(len(packed)):
            with self.assertRaises(ValueError):
                structure.unpack(packed[:end])
        inner = frame(3, b'x', codec.encode_entropy(structure.pack(b'x')))
        with self.assertRaises(ValueError):
            codec.decode(frame(3, b'x', inner))
        encoded = codec.encode_structured(data)
        for sample in [encoded[:-1], encoded + b'x', encoded[:-1] + bytes([encoded[-1] ^ 1])]:
            with self.assertRaises(ValueError):
                codec.decode(sample)
        with self.assertRaises(ValueError):
            structure.pack(b'x' * 131073)
        with self.assertRaises(ValueError):
            structure.pack(b'1234', min_digits=0)


if __name__ == '__main__':
    unittest.main()
