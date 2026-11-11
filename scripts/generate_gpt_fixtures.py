"""Generate synthetic GPT golden metadata with Python uuid/zlib, no product writer."""
import hashlib
import json
import struct
import uuid
import zlib
from pathlib import Path


def collision_tail(entries):
    # Independent adversarial fixture: change a name, solve four trailing bytes
    # over GF(2) so the entire different array retains the original CRC32.
    altered = bytearray(entries)
    altered[80] = 1
    base = zlib.crc32(altered)
    columns = []
    for bit in range(32):
        trial = bytearray(altered)
        trial[-4 + bit // 8] ^= 1 << (bit % 8)
        columns.append(zlib.crc32(trial) ^ base)
    basis = {}
    for bit, value in enumerate(columns):
        combination = 1 << bit
        while value:
            pivot = value.bit_length() - 1
            if pivot not in basis:
                basis[pivot] = value, combination
                break
            prior, mask = basis[pivot]
            value ^= prior
            combination ^= mask
    target, solution = zlib.crc32(entries) ^ base, 0
    while target:
        value, mask = basis[target.bit_length() - 1]
        target ^= value
        solution ^= mask
    for bit in range(32):
        if solution & (1 << bit):
            altered[-4 + bit // 8] ^= 1 << (bit % 8)
    assert altered != entries and zlib.crc32(altered) == zlib.crc32(entries)
    return altered[-4:].hex()


def generate(sector):
    blocks = 128
    count = 128
    table_sectors = count * 128 // sector
    first, last = 2 + table_sectors, blocks - table_sectors - 2
    disk = uuid.UUID('13a8b223-8b76-48e4-b508-5afbe5e15eda')
    ids = [uuid.UUID('dd02269d-8f0d-45df-91c1-d70f7a65a577'), uuid.UUID('5d18b153-d524-43a5-94bc-3bd6ce7b3238')]
    types = [uuid.UUID('c12a7328-f81f-11d2-ba4b-00a0c93ec93b'), uuid.UUID('0fc63daf-8483-4772-8e79-3d69d8477de4')]
    entries = bytearray(count * 128)
    partitions = []
    start = first + 2
    for index in range(2):
        end = start + 15
        struct.pack_into('<16s16sQQQ', entries, index * 128, types[index].bytes_le, ids[index].bytes_le, start, end, 0)
        partitions.append(dict(entryNumber=index + 1, typeId=str(types[index]), partitionId=str(ids[index]), offset=start * sector, length=16 * sector))
        start = end + 1
    mbr = bytearray(sector)
    struct.pack_into('<B3sB3sII', mbr, 446, 0, b'\0\2\0', 0xee, b'\xff\xff\xff', 1, blocks - 1)
    mbr[510:512] = b'\x55\xaa'

    def header(current, alternate, table):
        result = bytearray(sector)
        struct.pack_into('<8sIIIIQQQQ16sQIII', result, 0, b'EFI PART', 0x10000, 92, 0, 0,
                         current, alternate, first, last, disk.bytes_le, table, count, 128, zlib.crc32(entries))
        struct.pack_into('<I', result, 16, zlib.crc32(result[:92]))
        return result

    primary = header(1, blocks - 1, 2)
    backup_lba = blocks - table_sectors - 1
    backup = header(blocks - 1, 1, backup_lba)
    raw = bytearray(blocks * sector)
    for offset, region in [(0, mbr), (sector, primary), (sector * 2, entries),
                           (sector * backup_lba, entries), (sector * (blocks - 1), backup)]:
        raw[offset:offset + len(region)] = region
    root = hashlib.sha256(b'VaultSync.GptLayout.v1\0' + struct.pack('<IQ', sector, len(raw)) + mbr + primary + entries + entries + backup).hexdigest()
    return dict(sector=sector, capacity=len(raw), diskId=str(disk), firstUsableLba=first, lastUsableLba=last,
                mbrHex=mbr.hex(), primaryHex=primary.hex(), backupHex=backup.hex(), entriesHex=entries.hex(),
                collisionTailHex=collision_tail(entries), metadataSha256=root, sourceSha256=hashlib.sha256(raw).hexdigest(), partitions=partitions)


if __name__ == '__main__':
    output = Path(__file__).resolve().parents[1] / 'tests/VaultSync.Core.Tests/Fixtures/disk-gpt-v1.json'
    output.write_text(json.dumps([generate(512), generate(4096)], indent=2) + '\n')
