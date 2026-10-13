import hashlib
import importlib.util
import random
import struct
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('compression_prototype', ROOT / 'experiments/compression/codec.py')
codec = importlib.util.module_from_spec(spec)
spec.loader.exec_module(codec)


def frame(mode, size, payload, logical=b''):
    prefix = codec.PREFIX.pack(codec.MAGIC, mode, size, len(payload), hashlib.sha256(logical).digest())
    return prefix + hashlib.sha256(prefix + payload).digest() + payload


class CompressionPrototypeTests(unittest.TestCase):
    def test_hand_written_overlapping_match_fixture(self):
        self.assertEqual(b'aaaaa', codec.decode(frame(1, 5, b'\x00a\x80\x01\x00', b'aaaaa')))

    def test_roundtrip_empty_boundaries_random_text_and_overlapping_matches(self):
        generator = random.Random(1996)
        for size in (0, 1, 3, 4, 127, 128, 129, 131, 132, 65535, 131072):
            for data in (b'A' * size, bytes(generator.randrange(256) for _ in range(size)),
                         (b'%YAML prefab component guid: abcdef\n' * (size // 35 + 1))[:size]):
                with self.subTest(size=size, digest=hashlib.sha256(data).hexdigest()):
                    self.assertEqual(data, codec.decode(codec.encode(data)))

    def test_randomized_roundtrip(self):
        generator = random.Random(786)
        for _ in range(80):
            size = generator.randrange(4096)
            alphabet = generator.randrange(1, 257)
            data = bytes(generator.randrange(alphabet) for _ in range(size))
            self.assertEqual(data, codec.decode(codec.encode(data)))

    def test_raw_fallback_never_expands_payload(self):
        generator = random.Random(123)
        data = bytes(generator.randrange(256) for _ in range(16384))
        result = codec.encode(data)
        self.assertEqual(0, codec.PREFIX.unpack_from(result)[1])
        self.assertEqual(len(data) + codec.HEADER_BYTES, len(result))

    def test_encoder_rejects_oversized_blocks(self):
        with self.assertRaises(ValueError):
            codec.encode(b'a' * (codec.MAX_BLOCK_BYTES + 1))

    def test_truncation_trailing_bytes_unknown_magic_and_mutated_body_fail(self):
        encoded = codec.encode(b'abc' * 500)
        samples = (encoded[:-1], encoded + b'x', b'BADMAGIC' + encoded[8:],
                   encoded[:-1] + bytes([encoded[-1] ^ 1]))
        for sample in samples:
            with self.assertRaises(ValueError):
                codec.decode(sample)

    def test_forged_header_limits_and_unknown_mode_are_rejected(self):
        for sample in (frame(2, 0, b''), frame(0, codec.MAX_BLOCK_BYTES + 1, b''),
                       frame(0, 2, b'a', b'a'), frame(1, 0, b'\x00a')):
            with self.assertRaises(ValueError):
                codec.decode(sample)

    def test_invalid_tokens_rejected_even_with_valid_encoded_checksum(self):
        for payload in (b'\x80\x00\x00', b'\x80\x01\x00', b'\x7fA',
                        b'\x00a\x80', b'\x00a\xff\x01\x00'):
            with self.assertRaises(ValueError):
                codec.decode(frame(1, 5, payload, b'aaaaa'))

    def test_logical_digest_mismatch_is_not_hidden_by_valid_encoded_digest(self):
        with self.assertRaises(ValueError):
            codec.decode(frame(0, 4, b'data', b'else'))

    def test_research_codec_is_not_referenced_by_shipping_projects(self):
        for path in (ROOT / 'src').rglob('*.csproj'):
            self.assertNotIn('experiments/compression', path.read_text())
