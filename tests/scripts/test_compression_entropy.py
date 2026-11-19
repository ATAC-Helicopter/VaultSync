import hashlib
import random
import unittest

from experiments.compression import codec, entropy


class CompressionEntropyTests(unittest.TestCase):
    def test_random_and_boundary_roundtrips_for_both_encoders(self):
        randomizer = random.Random(1996)
        for size in (0, 1, 127, 128, 129, 4096, 131072):
            for data in (b'A' * size, randomizer.randbytes(size),
                         (b'component guid: abcdef speed: 100\n' * (size // 33 + 1))[:size]):
                self.assertEqual(data, entropy.decompress_tokens(entropy.compress_tokens(data)))
                self.assertEqual(data, codec.decode(codec.encode_entropy(data)))

    def test_handwritten_canonical_fixture(self):
        lengths = bytearray(256)
        lengths[ord('a')] = 1
        lengths[ord('b')] = 1
        payload = entropy.HEADER.pack(4, 4) + lengths + b'\x60'
        self.assertEqual(b'abba', entropy.decompress_tokens(payload))

    def test_entropy_is_selected_only_if_complete_payload_is_smaller(self):
        data = b''.join(f'--- !u!114 &{i}\nMonoBehaviour:\n  m_Name: Vehicle{i}\n  speed: {i % 200}\n'.encode()
                        for i in range(1200))
        original, dense = codec.encode(data), codec.encode_entropy(data)
        self.assertEqual(2, codec.PREFIX.unpack_from(dense)[1])
        self.assertLess(len(dense), len(original))
        self.assertEqual(data, codec.decode(dense))
        incompressible = random.Random(123).randbytes(131072)
        raw = codec.encode_entropy(incompressible)
        self.assertEqual(0, codec.PREFIX.unpack_from(raw)[1])
        self.assertEqual(len(incompressible) + codec.HEADER_BYTES, len(raw))

    def test_truncated_trailing_padding_and_declared_budgets_are_rejected(self):
        valid = entropy.compress_tokens(b'aaaaa')
        for invalid in (valid[:-1], valid + b'x', valid[:-1] + b'\x01',
                        entropy.HEADER.pack(entropy.MAX_TOKEN_BYTES + 1, 0) + bytes(256),
                        entropy.HEADER.pack(1, 25) + bytes(256) + bytes(4)):
            with self.assertRaises(ValueError):
                entropy.decompress_tokens(invalid)

    def test_invalid_codebooks_and_extra_symbols_fail(self):
        for assignments in ({97: 1, 98: 1, 99: 1}, {97: 2, 98: 2}, {97: 25}, {97: 2}, {}):
            lengths = bytearray(256)
            for symbol, length in assignments.items():
                lengths[symbol] = length
            with self.assertRaises(ValueError):
                entropy.decompress_tokens(entropy.HEADER.pack(1, 1) + lengths + b'\0')
        lengths = bytearray(256)
        lengths[97] = 1
        with self.assertRaises(ValueError):
            entropy.decompress_tokens(entropy.HEADER.pack(1, 2) + lengths + b'\0')
        with self.assertRaises(ValueError):
            entropy.decompress_tokens(entropy.HEADER.pack(1, 1) + lengths + b'\x80')

    def test_malformed_entropy_with_valid_outer_digest_does_not_bypass_decoder(self):
        payload = entropy.HEADER.pack(entropy.MAX_TOKEN_BYTES + 1, 0) + bytes(256)
        prefix = codec.PREFIX.pack(codec.MAGIC, 2, 1, len(payload), hashlib.sha256(b'a').digest())
        invalid = prefix + hashlib.sha256(prefix + payload).digest() + payload
        with self.assertRaises(ValueError):
            codec.decode(invalid)

    def test_encoder_refuses_unbounded_token_streams(self):
        with self.assertRaises(ValueError):
            entropy.compress_tokens(b'x' * (entropy.MAX_TOKEN_BYTES + 1))

    def test_valid_entropy_and_outer_hash_cannot_hide_invalid_lz_or_logical_content(self):
        for tokens, digest in ((b'\x00a\x80\x00\x00', hashlib.sha256(b'aaaaa').digest()),
                               (b'\x00a\x80\x01\x00', hashlib.sha256(b'wrong').digest())):
            payload = entropy.compress_tokens(tokens)
            prefix = codec.PREFIX.pack(codec.MAGIC, 2, 5, len(payload), digest)
            with self.assertRaises(ValueError):
                codec.decode(prefix + hashlib.sha256(prefix + payload).digest() + payload)
