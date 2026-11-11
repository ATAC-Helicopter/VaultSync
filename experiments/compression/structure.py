"""Bounded, byte-exact numeric context lanes. Disposable compression research."""
import re
import struct

MAX_BYTES = 128 * 1024
MAX_LANES = 32
HEADER = struct.Struct('<4sIIB')
MAGIC = b'VSL1'


def _put(value):
    result = bytearray()
    while value >= 128:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return result


def _get(data, offset, end):
    value = 0
    for shift in range(0, 63, 7):
        if offset >= end:
            raise ValueError('Truncated numeric lane')
        byte = data[offset]
        offset += 1
        value |= (byte & 127) << shift
        if byte < 128:
            if shift and byte == 0:
                raise ValueError('Noncanonical numeric lane integer')
            return value, offset
    raise ValueError('Numeric lane integer exceeds budget')


def pack(data, min_digits=1):
    """Return a reversible candidate, or None when the bounded stream would expand."""
    if min_digits not in (1, 4):
        raise ValueError('Unknown numeric extraction policy')
    if len(data) > MAX_BYTES:
        raise ValueError('Input exceeds research block limit')
    skeleton = bytearray()
    contexts, lanes, previous = {}, [], []
    cursor = 0
    for match in re.finditer(rb'[0-9]+', data):
        start, end = match.span()
        skeleton.extend(data[cursor:start].replace(b'\0', b'\0\0'))
        width = end - start
        if width > 18 or width < min_digits:
            skeleton.extend(data[start:end])
        else:
            context = data[max(cursor, start - 64):start]
            lane = contexts.get(context)
            if lane is None:
                if len(contexts) < MAX_LANES:
                    lane = len(contexts)
                    contexts[context] = lane
                    lanes.append(bytearray())
                    previous.append(0)
                else:
                    lane = MAX_LANES - 1
            value = int(data[start:end])
            delta = value - previous[lane]
            lanes[lane].extend(_put(2 * delta if delta >= 0 else -2 * delta - 1))
            previous[lane] = value
            skeleton.extend((0, lane + 1, width))
        cursor = end
        if len(skeleton) > MAX_BYTES:
            return None
    skeleton.extend(data[cursor:].replace(b'\0', b'\0\0'))
    size = HEADER.size + len(skeleton) + 4 * len(lanes) + sum(map(len, lanes))
    if size > MAX_BYTES:
        return None
    result = bytearray(HEADER.pack(MAGIC, len(data), len(skeleton), len(lanes)))
    result.extend(skeleton)
    for lane in lanes:
        result.extend(struct.pack('<I', len(lane)))
        result.extend(lane)
    return bytes(result)


def unpack(payload):
    if not HEADER.size <= len(payload) <= MAX_BYTES:
        raise ValueError('Invalid structured stream size')
    magic, logical, skeleton_size, count = HEADER.unpack_from(payload)
    if magic != MAGIC or logical > MAX_BYTES or count > MAX_LANES:
        raise ValueError('Invalid structured stream header')
    start, end = HEADER.size, HEADER.size + skeleton_size
    if end > len(payload):
        raise ValueError('Truncated skeleton')
    lane_starts, lane_ends = [], []
    offset = end
    for _ in range(count):
        if offset + 4 > len(payload):
            raise ValueError('Truncated lane header')
        size = struct.unpack_from('<I', payload, offset)[0]
        offset += 4
        if offset + size > len(payload):
            raise ValueError('Truncated lane payload')
        lane_starts.append(offset)
        lane_ends.append(offset + size)
        offset += size
    if offset != len(payload):
        raise ValueError('Trailing structured bytes')
    previous = [0] * count
    output = bytearray()
    offset = start
    while offset < end:
        byte = payload[offset]
        offset += 1
        if byte:
            if len(output) >= logical:
                raise ValueError('Structured output exceeds declared budget')
            output.append(byte)
            continue
        if offset >= end:
            raise ValueError('Truncated skeleton escape')
        lane = payload[offset]
        offset += 1
        if lane == 0:
            if len(output) >= logical:
                raise ValueError('Structured output exceeds declared budget')
            output.append(0)
            continue
        if lane > count or offset >= end:
            raise ValueError('Invalid skeleton lane')
        width = payload[offset]
        offset += 1
        if not 1 <= width <= 18 or len(output) + width > logical:
            raise ValueError('Invalid numeric spelling width')
        index = lane - 1
        encoded, lane_starts[index] = _get(payload, lane_starts[index], lane_ends[index])
        delta = -(encoded // 2) - 1 if encoded & 1 else encoded // 2
        value = previous[index] + delta
        if not 0 <= value < 10 ** width:
            raise ValueError('Numeric value cannot fit its original spelling')
        previous[index] = value
        output.extend(str(value).zfill(width).encode('ascii'))
    if len(output) != logical or lane_starts != lane_ends:
        raise ValueError('Incomplete structure or unconsumed numeric lane')
    return bytes(output)
