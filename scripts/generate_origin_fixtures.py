"""Independent provisional schema-2 metadata; no product encoder is imported."""
import hashlib
import json
import struct
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def generate(gpt):
    sector, capacity = gpt['sector'], gpt['capacity']
    raw = bytearray(capacity)
    entries = bytes.fromhex(gpt['entriesHex'])
    for offset, data in [(0, bytes.fromhex(gpt['mbrHex'])), (sector, bytes.fromhex(gpt['primaryHex'])),
                         (sector * 2, entries), (capacity - sector - len(entries), entries),
                         (capacity - sector, bytes.fromhex(gpt['backupHex']))]:
        raw[offset:offset + len(data)] = data
    image = uuid.UUID('a2b3f2a2-492c-4105-b7aa-c5b734b368a0')
    capture = uuid.UUID('eab839fb-1a08-4c0b-b40a-7a37b546517f')
    part = uuid.UUID('24c69ffa-e6b9-44a8-b215-364d8d8e9ccd')
    source = uuid.UUID('03e993ab-24b3-45aa-9c7a-bca91747251f')
    observation = uuid.UUID('88b50cc4-0268-435c-a6a0-97b20f754ac1')
    stamp = 1792051200000  # Fixed synthetic fixture timestamp, not execution evidence.
    version, commit = '1.9.0-fixture', '1234567890abcdef1234567890abcdef12345678'
    origin = struct.pack('<II16s16sIq16s32sQQI32s64s', 1, 1, source.bytes, observation.bytes,
                         sector, stamp, uuid.UUID(gpt['diskId']).bytes, bytes.fromhex(gpt['metadataSha256']),
                         gpt['firstUsableLba'], gpt['lastUsableLba'], len(gpt['partitions']), version.encode(), commit.encode())
    assert len(origin) == 216
    for p in gpt['partitions']:
        origin += struct.pack('<I16s16sQQQI', p['entryNumber'], uuid.UUID(p['typeId']).bytes,
                              uuid.UUID(p['partitionId']).bytes, p['offset'], p['length'], 0, 0)
    manifest = struct.pack('<I16s16sQQIII', 2, image.bytes, capture.bytes, 1, capacity, sector, 1, 1)
    manifest += origin
    manifest += struct.pack('<16sQ32s', part.bytes, capacity, hashlib.sha256(raw).digest())
    manifest += struct.pack('<QQI16sQ', 0, capacity, 0, part.bytes, 0)
    root = hashlib.sha256(manifest).digest()
    header = struct.pack('<8sHHIII', b'VSIMAGE\0', 1, 0, 0, len(manifest), 0)
    completion = struct.pack('<8s16sQ32s', b'VSCOMP\0\0', image.bytes, 1, root)
    return dict(sector=sector, metadataHex=(header+manifest+completion).hex(), partId=str(part),
                sourceId=str(source), observationId=str(observation), timestampMilliseconds=stamp,
                toolVersion=version, toolSourceCommit=commit, manifestRoot=root.hex())


if __name__ == '__main__':
    directory = ROOT / 'tests/VaultSync.Core.Tests/Fixtures'
    fixtures = json.loads((directory / 'disk-gpt-v1.json').read_text())
    (directory / 'disk-image-origin-v2.json').write_text(json.dumps([generate(g) for g in fixtures], indent=2)+'\n')
