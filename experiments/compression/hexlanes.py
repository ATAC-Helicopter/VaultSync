"""Byte-exact packing of standalone 32-digit hex lexemes. Research only."""
import re
import struct

MAX_BYTES = 128 * 1024
HEADER = struct.Struct('<4sI')
MAGIC = b'VSH1'
LEXEMES = re.compile(rb'(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])')


def pack(data):
    if len(data) > MAX_BYTES:
        raise ValueError('Input exceeds research block limit')
    result = bytearray(HEADER.pack(MAGIC, len(data)))
    cursor = 0
    found = False
    for match in LEXEMES.finditer(data):
        found = True
        result.extend(data[cursor:match.start()].replace(b'\0', b'\0\0'))
        spelling = match.group()
        lower = spelling.lower()
        tag = 1 if spelling == lower else 2 if spelling == spelling.upper() else 3
        result.extend((0, tag))
        result.extend(bytes.fromhex(lower.decode('ascii')))
        if tag == 3:
            mask = sum(1 << index for index, byte in enumerate(spelling) if 65 <= byte <= 70)
            result.extend(struct.pack('<I', mask))
        cursor = match.end()
        if len(result) > MAX_BYTES:
            return None
    result.extend(data[cursor:].replace(b'\0', b'\0\0'))
    return bytes(result) if found and len(result) <= MAX_BYTES else None


def unpack(payload):
    if not HEADER.size <= len(payload) <= MAX_BYTES:
        raise ValueError('Invalid hex stream size')
    magic, logical = HEADER.unpack_from(payload)
    if magic != MAGIC or logical > MAX_BYTES:
        raise ValueError('Invalid hex stream header')
    output = bytearray()
    offset = HEADER.size
    while offset < len(payload):
        byte = payload[offset]
        offset += 1
        if byte:
            spelling = bytes((byte,))
        else:
            if offset == len(payload):
                raise ValueError('Truncated hex escape')
            tag = payload[offset]
            offset += 1
            if tag == 0:
                spelling = b'\0'
            elif tag in (1, 2, 3):
                size = 16 + (4 if tag == 3 else 0)
                if offset + size > len(payload):
                    raise ValueError('Truncated hex lexeme')
                spelling = payload[offset:offset + 16].hex().encode('ascii')
                if tag == 2:
                    spelling = spelling.upper()
                    if spelling == spelling.lower():
                        raise ValueError('Noncanonical uppercase hex spelling')
                elif tag == 3:
                    mask = struct.unpack_from('<I', payload, offset + 16)[0]
                    letters = bytearray(spelling)
                    for index in range(32):
                        if mask & (1 << index):
                            if not 97 <= letters[index] <= 102:
                                raise ValueError('Hex case mask marks a digit')
                            letters[index] -= 32
                    spelling = bytes(letters)
                    if spelling in (spelling.lower(), spelling.upper()):
                        raise ValueError('Noncanonical mixed hex spelling')
                offset += size
            else:
                raise ValueError('Unknown hex escape')
        if len(output) + len(spelling) > logical:
            raise ValueError('Hex output exceeds declared budget')
        output.extend(spelling)
    if len(output) != logical:
        raise ValueError('Incomplete hex output')
    return bytes(output)
