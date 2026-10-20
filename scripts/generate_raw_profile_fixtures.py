"""Permanent raw-block reader corpus; Python encoding, no VaultSync codec imports."""
import hashlib
import json
import struct
from pathlib import Path

from generate_origin_fixtures import generate

ROOT = Path(__file__).resolve().parents[1]


def rehash(data):
    data[-32:] = hashlib.sha256(data[24:-64]).digest()


def cases(gpt):
    source = generate(gpt)
    valid = bytearray.fromhex(source['metadataHex'])
    struct.pack_into('<I', valid, 92, 2)  # Origin kind: claimed offline block device.
    rehash(valid)
    result = []

    def add(name, data, state, reads_payload=False, payload_fault=None):
        result.append(dict(name=name, sector=gpt['sector'], metadataHex=data.hex(),
                           state=state, readsPayload=reads_payload, payloadFault=payload_fault))

    add('valid', valid, 'ContentAndLayoutVerified', True)
    add('fixture-legacy', bytearray.fromhex(source['metadataHex']), 'Unsupported')
    add('incomplete', valid[:-1], 'Incomplete')
    add('missing-completion', valid[:-64], 'Incomplete')
    add('trailing', valid + b'\0', 'Corrupt')
    bad = valid.copy(); bad[-1] ^= 1
    add('root-corrupt', bad, 'Corrupt')
    for name, offset, fmt, value, state in [
        ('future-envelope', 8, '<H', 2, 'Unsupported'),
        ('future-schema', 24, '<I', 3, 'Unsupported'),
        ('future-origin', 92, '<I', 3, 'Unsupported'),
        ('capacity-overflow', 68, '<Q', 8 * 1024**4 + gpt['sector'], 'Invalid'),
        ('zero-generation', 60, '<Q', 0, 'Invalid'),
        ('invalid-sector', 76, '<I', 1024, 'Invalid'),
        ('multiple-parts', 80, '<I', 2, 'Unsupported'),
    ]:
        bad = valid.copy(); struct.pack_into(fmt, bad, offset, value); rehash(bad)
        add(name, bad, state)
    bad = valid.copy(); bad[140] ^= 1; rehash(bad)  # Syntactically valid, false disk GUID.
    add('forged-layout', bad, 'Corrupt', True)
    add('payload-corrupt', valid, 'Corrupt', True, 'flip-middle')
    bad = valid[:24]; struct.pack_into('<I', bad, 16, 16 * 1024**2 - 87)
    add('total-metadata-limit', bad, 'Invalid')
    return result


if __name__ == '__main__':
    directory = ROOT / 'tests/VaultSync.Core.Tests/Fixtures'
    gpts = json.loads((directory / 'disk-gpt-v1.json').read_text())
    output = [case for gpt in gpts for case in cases(gpt)]
    (directory / 'disk-image-raw-profile-v1.json').write_text(json.dumps(output, indent=2) + '\n')
