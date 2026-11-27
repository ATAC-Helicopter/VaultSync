import hashlib
import random
import struct
import unittest
from unittest.mock import patch
from experiments.compression import codec, hexlanes, selection


def frame(mode, logical, payload):
    prefix = codec.PREFIX.pack(codec.MAGIC, mode, len(logical), len(payload), hashlib.sha256(logical).digest())
    return prefix + hashlib.sha256(prefix + payload).digest() + payload


def stream(logical, body):
    return struct.pack('<4sI', b'VSH1', logical) + body


class HybridCompressionTests(unittest.TestCase):
    def test_independent_hex_spelling_fixtures(self):
        raw = bytes.fromhex('abcdef0123456789abcdef0123456789')
        self.assertEqual(hexlanes.unpack(stream(32, b'\0\1' + raw)), b'abcdef0123456789abcdef0123456789')
        self.assertEqual(hexlanes.unpack(stream(32, b'\0\2' + raw)), b'ABCDEF0123456789ABCDEF0123456789')
        self.assertEqual(hexlanes.unpack(stream(34, b'\0\3' + raw + b'\1\0\0\0' + b'\0\0\n')),
                         b'Abcdef0123456789abcdef0123456789\0\n')

    def test_boundary_spelling_and_binary_roundtrips(self):
        data = [b'', bytes(range(256)), b'\0' * 131072,
                b'0' * 31, b'a' * 33, b'F' * 64,
                b'\0guid: 0123456789abcdefABCDEF0123456789\r\n',
                b'0' * 32 + b'\n' + b'F' * 32,
                (b'x' * 1024 + b'\nabcdef0123456789abcdef0123456789\n') * 120]
        for original in data:
            transformed = hexlanes.pack(original)
            if transformed is not None:
                self.assertEqual(hexlanes.unpack(transformed), original)
            encoded = codec.encode_hybrid(original)
            self.assertEqual(codec.decode(encoded), original)
            self.assertLessEqual(len(encoded), len(codec.encode_entropy(original)))

    def test_random_mixed_case_roundtrips(self):
        rng = random.Random(199616)
        for index in range(25):
            original = rng.randbytes(index * 53) + b'\n'
            for _ in range(index):
                spelling = bytearray(rng.randbytes(16).hex().encode())
                for position in range(32):
                    if rng.randrange(2):
                        spelling[position:position + 1] = bytes(spelling[position:position + 1]).upper()
                original += bytes(spelling) + b'\0\r\n'
            transformed = hexlanes.pack(original)
            if transformed is not None:
                self.assertEqual(hexlanes.unpack(transformed), original)
            self.assertEqual(codec.decode(codec.encode_hybrid(original)), original)

    def test_selector_bounds_and_routes(self):
        self.assertEqual(selection.select(b''), 'baseline')
        self.assertEqual(selection.select(bytes(range(256)) * 500), 'baseline')
        self.assertEqual(selection.select(b'speed: 123\n' * 100), 'numeric')
        self.assertEqual(selection.select(b'guid: abcdef0123456789abcdef0123456789\n' * 100), 'hex')
        observed = []
        class Pattern:
            def finditer(self, sample):
                observed.append(len(sample))
                return iter(())
        with patch.object(selection, 'LEXEMES', Pattern()):
            self.assertEqual(selection.select(b'x' * 131072), 'baseline')
        self.assertEqual(len(observed), 4)
        self.assertLessEqual(sum(observed), 8192)
        for function in (hexlanes.pack, selection.select, codec.encode_hybrid):
            with self.assertRaises(ValueError):
                function(b'x' * 131073)

    def test_only_selected_transform_family_is_attempted(self):
        with patch.object(codec, 'pack_structure', side_effect=AssertionError('numeric transform attempted')):
            codec.encode_hybrid(b'guid: abcdef0123456789abcdef0123456789\n' * 100)
        with patch.object(codec, 'pack_hex', side_effect=AssertionError('hex transform attempted')):
            codec.encode_hybrid(b'speed: 123\n' * 100)
        with patch.object(codec, 'pack_hex', side_effect=AssertionError('hex transform attempted')), \
                patch.object(codec, 'pack_structure', side_effect=AssertionError('numeric transform attempted')):
            codec.encode_hybrid(bytes(range(256)) * 500)

    def test_guid_mode_is_useful_and_deterministic(self):
        rng = random.Random(199617)
        original = b''.join(b'guid: ' + rng.randbytes(16).hex().encode() + b'\n' for _ in range(2000))
        encoded = codec.encode_hybrid(original)
        self.assertEqual(encoded[8], 4)
        self.assertEqual(codec.decode(encoded), original)
        self.assertEqual(encoded, codec.encode_hybrid(original))
        self.assertLess(len(encoded), len(codec.encode_entropy(original)))

    def test_bad_hex_with_valid_outer_hashes_refuses(self):
        raw = bytes.fromhex('abcdef0123456789abcdef0123456789')
        bad = [b'', stream(1, b'\0'), stream(1, b'\0\4'), stream(1, b'\0\1'),
               stream(1, b'\0\1' + raw), stream(32, b'\0\2' + bytes(16)),
               stream(32, b'\0\3' + raw + struct.pack('<I', 1 << 6)),  # digit case bit
               stream(32, b'\0\3' + raw + bytes(4)),  # mixed tag without mixed letters
               stream(32, b'\0\3' + raw + struct.pack('<I', 0x3f003f)),  # all letters uppercase
               stream(0, b'x'), stream(0, b'\0\0'), stream(131073, b''), stream(0, b'') + b'extra']
        for payload in bad:
            with self.subTest(payload=payload):
                with self.assertRaises(ValueError):
                    hexlanes.unpack(payload)
                with self.assertRaises(ValueError):
                    codec.decode(frame(4, b'x', codec.encode_entropy(payload)))

    def test_truncation_and_transform_recursion_refuse(self):
        original = b'\0Abcdef0123456789abcdef0123456789\r\n'
        packed = hexlanes.pack(original)
        for end in range(len(packed)):
            with self.assertRaises(ValueError):
                hexlanes.unpack(packed[:end])
        for outer in (3, 4):
            for inner in (3, 4):
                nested = frame(inner, b'x', codec.encode_entropy(b'x'))
                with self.assertRaises(ValueError):
                    codec.decode(frame(outer, b'x', nested))
        encoded = frame(4, original, codec.encode_entropy(packed))
        for bad in (encoded[:-1], encoded + b'x', encoded[:-1] + bytes([encoded[-1] ^ 1])):
            with self.assertRaises(ValueError):
                codec.decode(bad)


if __name__ == '__main__':
    unittest.main()
