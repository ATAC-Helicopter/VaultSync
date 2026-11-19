"""Bounded canonical Huffman experiment for the LZ77 token stream."""
import heapq
import struct
from collections import Counter

MAX_TOKEN_BYTES = 128 * 1024 + 1024
MAX_CODE_BITS = 24
HEADER = struct.Struct('<II')
HEADER_BYTES = HEADER.size + 256


def _codes(lengths):
    symbols = sorted((length, symbol) for symbol, length in enumerate(lengths) if length)
    if not symbols or symbols[-1][0] > MAX_CODE_BITS:
        raise ValueError('Invalid Huffman code lengths')
    code, previous = 0, 0
    result = {}
    for length, symbol in symbols:
        code <<= length - previous
        if code >= 1 << length:
            raise ValueError('Oversubscribed Huffman table')
        result[symbol] = (code, length)
        code += 1
        previous = length
    if len(symbols) == 1:
        if previous != 1:
            raise ValueError('Invalid single-symbol Huffman table')
    elif code != 1 << previous:
        raise ValueError('Incomplete Huffman table')
    return result


def compress_tokens(data: bytes) -> bytes:
    if len(data) > MAX_TOKEN_BYTES:
        raise ValueError('Token stream exceeds fixed limit')
    lengths = bytearray(256)
    if not data:
        return HEADER.pack(0, 0) + lengths
    frequencies = Counter(data)
    heap = [(count, symbol, symbol) for symbol, count in sorted(frequencies.items())]
    heapq.heapify(heap)
    sequence = 256
    while len(heap) > 1:
        left = heapq.heappop(heap)
        right = heapq.heappop(heap)
        heapq.heappush(heap, (left[0] + right[0], sequence, (left[2], right[2])))
        sequence += 1
    pending = [(heap[0][2], 0)]
    while pending:
        node, depth = pending.pop()
        if isinstance(node, int):
            lengths[node] = max(1, depth)
        else:
            pending.extend((child, depth + 1) for child in node)
    codes = _codes(lengths)
    output = bytearray()
    buffer, buffered = 0, 0
    bit_count = 0
    for value in data:
        code, bits = codes[value]
        buffer = (buffer << bits) | code
        buffered += bits
        bit_count += bits
        while buffered >= 8:
            buffered -= 8
            output.append((buffer >> buffered) & 255)
        buffer &= (1 << buffered) - 1
    if buffered:
        output.append(buffer << (8 - buffered))
    return HEADER.pack(len(data), bit_count) + lengths + output


def decompress_tokens(payload: bytes) -> bytes:
    if len(payload) < HEADER_BYTES or len(payload) > HEADER_BYTES + MAX_TOKEN_BYTES * 3:
        raise ValueError('Invalid entropy payload length')
    count, bit_count = HEADER.unpack_from(payload)
    if count > MAX_TOKEN_BYTES or bit_count > count * MAX_CODE_BITS:
        raise ValueError('Entropy output exceeds fixed limit')
    if len(payload) != HEADER_BYTES + (bit_count + 7) // 8:
        raise ValueError('Truncated entropy payload or trailing bytes')
    lengths = payload[HEADER.size:HEADER_BYTES]
    if not count:
        if bit_count or any(lengths):
            raise ValueError('Invalid empty entropy stream')
        return b''
    if bit_count < count:
        raise ValueError('Invalid entropy bit count')
    if bit_count % 8 and payload[-1] & ((1 << (8 - bit_count % 8)) - 1):
        raise ValueError('Nonzero entropy padding')
    codes = _codes(lengths)
    lookup = {(length, code): symbol for symbol, (code, length) in codes.items()}
    longest = max(lengths)
    output = bytearray()
    code, length = 0, 0
    for position in range(bit_count):
        code = (code << 1) | ((payload[HEADER_BYTES + position // 8] >> (7 - position % 8)) & 1)
        length += 1
        symbol = lookup.get((length, code))
        if symbol is not None:
            if len(output) >= count:
                raise ValueError('Entropy output length overflow')
            output.append(symbol)
            code, length = 0, 0
        elif length >= longest:
            raise ValueError('Invalid entropy code')
    if length or len(output) != count:
        raise ValueError('Incomplete entropy output')
    return bytes(output)
