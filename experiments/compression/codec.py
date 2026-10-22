"""VaultSync compression research. Experimental framing; never a backup format."""
from __future__ import annotations
import hashlib
import struct
from experiments.compression.entropy import compress_tokens, decompress_tokens
from experiments.compression.structure import pack as pack_structure, unpack as unpack_structure
from experiments.compression.hexlanes import pack as pack_hex, unpack as unpack_hex
from experiments.compression.selection import select

MAGIC = b'VSCPROT0'
MAX_BLOCK_BYTES = 128 * 1024
WINDOW_BYTES = 65535
PREFIX = struct.Struct('<8sBII32s')
HEADER_BYTES = PREFIX.size + 32


def _slot(data: bytes, offset: int) -> int:
    word = int.from_bytes(data[offset:offset + 4], 'little')
    return ((word * 2654435761) & 0xffffffff) >> 18


def _encode_tokens(data: bytes) -> bytes:
    table = [-1] * 16384
    result = bytearray()
    literals = bytearray()
    offset = 0

    def flush() -> None:
        if literals:
            result.append(len(literals) - 1)
            result.extend(literals)
            literals.clear()

    while offset < len(data):
        length = 0
        previous = -1
        if offset + 4 <= len(data):
            slot = _slot(data, offset)
            previous = table[slot]
            table[slot] = offset
            if previous >= 0 and offset - previous <= WINDOW_BYTES:
                limit = min(131, len(data) - offset)
                while length < limit and data[previous + length] == data[offset + length]:
                    length += 1
        if length >= 4:
            flush()
            result.append(128 + length - 4)
            result.extend(struct.pack('<H', offset - previous))
            offset += length
        else:
            literals.append(data[offset])
            offset += 1
            if len(literals) == 128:
                flush()
    flush()
    return bytes(result)


def encode(data: bytes) -> bytes:
    return _encode_frame(data, use_entropy=False)


def encode_entropy(data: bytes) -> bytes:
    return _encode_frame(data, use_entropy=True)


def encode_structured(data: bytes) -> bytes:
    return _encode_frame(data, use_entropy=True, use_structure=True)


def encode_hybrid(data: bytes) -> bytes:
    strategy = select(data)
    return _encode_frame(data, use_entropy=True, use_structure=strategy == 'numeric', use_hex=strategy == 'hex')


def _encode_frame(data: bytes, use_entropy: bool, use_structure: bool = False, use_hex: bool = False) -> bytes:
    if len(data) > MAX_BLOCK_BYTES:
        raise ValueError('Experimental block exceeds the fixed limit')
    candidate = _encode_tokens(data)
    mode, payload = (1, candidate) if len(candidate) < len(data) else (0, data)
    if use_entropy:
        entropy = compress_tokens(candidate)
        if len(entropy) < len(payload):
            mode, payload = 2, entropy
    if use_structure:
        previous_transform = None
        for min_digits in (1, 4):
            transformed = pack_structure(data, min_digits=min_digits)
            if transformed is not None and transformed != previous_transform:
                structured = _encode_frame(transformed, use_entropy=True)
                if len(structured) < len(payload):
                    mode, payload = 3, structured
            previous_transform = transformed
    if use_hex:
        transformed = pack_hex(data)
        if transformed is not None:
            packed = _encode_frame(transformed, use_entropy=True)
            if len(packed) < len(payload):
                mode, payload = 4, packed
    prefix = PREFIX.pack(MAGIC, mode, len(data), len(payload), hashlib.sha256(data).digest())
    # Binds metadata and encoded bytes. This detects corruption, not malicious forgery.
    digest = hashlib.sha256(prefix + payload).digest()
    return prefix + digest + payload


def decode(frame: bytes) -> bytes:
    return _decode_frame(frame, allow_structure=True)


def _decode_frame(frame: bytes, allow_structure: bool) -> bytes:
    if len(frame) < HEADER_BYTES or len(frame) > HEADER_BYTES + MAX_BLOCK_BYTES:
        raise ValueError('Invalid experimental frame size')
    magic, mode, logical_size, stored_size, logical_digest = PREFIX.unpack_from(frame)
    if magic != MAGIC or mode not in ((0, 1, 2, 3, 4) if allow_structure else (0, 1, 2)):
        raise ValueError('Unknown experimental format or codec')
    if logical_size > MAX_BLOCK_BYTES or stored_size > MAX_BLOCK_BYTES:
        raise ValueError('Declared block exceeds the fixed limit')
    if len(frame) != HEADER_BYTES + stored_size:
        raise ValueError('Truncated frame or trailing bytes')
    payload = frame[HEADER_BYTES:]
    if hashlib.sha256(frame[:PREFIX.size] + payload).digest() != frame[PREFIX.size:HEADER_BYTES]:
        raise ValueError('Encoded frame integrity mismatch')
    if mode in (3, 4):
        # One structural layer only, even when outer checksums are recomputed.
        unpack = unpack_structure if mode == 3 else unpack_hex
        output = unpack(_decode_frame(payload, allow_structure=False))
    elif mode == 0:
        if stored_size != logical_size:
            raise ValueError('Stored block length mismatch')
        output = payload
    else:
        if mode == 2:
            payload = decompress_tokens(payload)
            stored_size = len(payload)
        output_buffer = bytearray()
        offset = 0
        while offset < stored_size:
            tag = payload[offset]
            offset += 1
            if tag < 128:
                count = tag + 1
                if offset + count > stored_size or len(output_buffer) + count > logical_size:
                    raise ValueError('Invalid literal run')
                output_buffer.extend(payload[offset:offset + count])
                offset += count
            else:
                count = tag - 128 + 4
                if offset + 2 > stored_size or len(output_buffer) + count > logical_size:
                    raise ValueError('Invalid match length')
                distance = struct.unpack_from('<H', payload, offset)[0]
                offset += 2
                if distance == 0 or distance > len(output_buffer):
                    raise ValueError('Invalid match distance')
                for _ in range(count):
                    output_buffer.append(output_buffer[-distance])
        output = bytes(output_buffer)
    if len(output) != logical_size or hashlib.sha256(output).digest() != logical_digest:
        raise ValueError('Logical block integrity mismatch')
    return output
