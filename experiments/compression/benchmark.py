"""Generated fixtures only; run from the repository root. No backup I/O."""
import argparse
import hashlib
import json
import platform
import random
import statistics
import subprocess
import sys
import time
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from scripts.safe_artifact_output import write_text_exclusive
from experiments.compression.codec import MAX_BLOCK_BYTES, decode, encode, encode_entropy, encode_structured, encode_hybrid
from experiments.compression.selection import select
from experiments.compression.hexlanes import pack as pack_hex, unpack as unpack_hex
from experiments.compression.structure import pack as pack_structure, unpack as unpack_structure


def structure_zlib(data):
    base = zlib.compress(data, 6)
    result = b'\0' + base
    for min_digits in (1, 4):
        transformed = pack_structure(data, min_digits=min_digits)
        if transformed is not None:
            candidate = b'\1' + zlib.compress(transformed, 6)
            if len(candidate) < len(result):
                result = candidate
    return result


def unstructure_zlib(frame):
    restored = zlib.decompress(frame[1:])
    return unpack_structure(restored) if frame[0] else restored



def hex_zlib(data):
    result = b'\0' + zlib.compress(data, 6)
    transformed = pack_hex(data)
    if transformed is not None:
        candidate = b'\1' + zlib.compress(transformed, 6)
        if len(candidate) < len(result):
            result = candidate
    return result


def unhex_zlib(frame):
    restored = zlib.decompress(frame[1:])
    return unpack_hex(restored) if frame[0] else restored


def hex_reference(compress, decompress):
    def encode(data):
        result = b'\0' + compress(data)
        transformed = pack_hex(data)
        if transformed is not None:
            candidate = b'\1' + compress(transformed)
            if len(candidate) < len(result):
                result = candidate
        return result

    def decode(frame):
        restored = decompress(frame[1:])
        return unpack_hex(restored) if frame[0] else restored

    return encode, decode


def run():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--zstandard', action='store_true', help='Include the optional external reference, not a product dependency')
    options = parser.parse_args()
    references = []
    zstd_version = None
    if options.zstandard:
        import zstandard
        zstd_version = {'pythonPackage': zstandard.__version__, 'library': zstandard.ZSTD_VERSION}
        for level in (3, 9):
            compressor = zstandard.ZstdCompressor(level=level, write_checksum=True)
            decompressor = zstandard.ZstdDecompressor()
            references.append((f'zstd-{level}', compressor.compress, decompressor.decompress))
            references.append((f'hex-zstd-{level}', *hex_reference(compressor.compress, decompressor.decompress)))
    rng = random.Random(1996)
    fixtures = {
        'unity-yaml': b''.join(
            f'--- !u!114 &{i}\nMonoBehaviour:\n  m_Name: Vehicle{i}\n  m_Enabled: 1\n  speed: {i % 200}\n'.encode()
            for i in range(6000)),
        'source-code': b''.join(
            f'public class Vehicle{i} {{ public int Speed => {i % 200}; }}\n'.encode()
            for i in range(8000)),
        'random-proxy': rng.randbytes(512 * 1024),  # NOSONAR: BUG-19034, reproducible public benchmark bytes; not secret material.
    }
    fixtures['already-deflated'] = zlib.compress(fixtures['unity-yaml'], 6)
    fixtures['numeric-json'] = b''.join(
        f'{{"tick":{i},"object":{1000000000+i},"x":{i%997},"y":{i%191}}}\n'.encode()
        for i in range(6000))
    fixtures['guid-heavy-yaml'] = b''.join(
        f'guid: {rng.randbytes(16).hex()}\nfileID: {i}\nname: Asset{i}\n'.encode()  # NOSONAR: BUG-19034, synthetic public GUID-shaped text; no production identity.
        for i in range(3500))
    rows = []
    for name, data in fixtures.items():
        blocks = [data[i:i + MAX_BLOCK_BYTES] for i in range(0, len(data), MAX_BLOCK_BYTES)]
        for label, compressor, decompressor in (
                ('prototype-v0', encode, decode),
                ('prototype-huffman', encode_entropy, decode),
                ('prototype-structure', encode_structured, decode),
                ('prototype-hybrid', encode_hybrid, decode),
                ('hex-zlib-6', hex_zlib, unhex_zlib),
                ('structure-zlib-6', structure_zlib, unstructure_zlib),
                ('zlib-1', lambda b: zlib.compress(b, 1), zlib.decompress),
                ('zlib-6', lambda b: zlib.compress(b, 6), zlib.decompress),
                ('zlib-9', lambda b: zlib.compress(b, 9), zlib.decompress), *references):
            encoding, decoding = [], []
            for iteration in range(6):
                started = time.perf_counter_ns()
                stored = [compressor(b) for b in blocks]
                encoded_ms = (time.perf_counter_ns() - started) / 1e6
                started = time.perf_counter_ns()
                restored = b''.join(decompressor(b) for b in stored)
                decoded_ms = (time.perf_counter_ns() - started) / 1e6
                if restored != data:
                    raise RuntimeError('Roundtrip failed')
                if iteration:
                    encoding.append(encoded_ms)
                    decoding.append(decoded_ms)
            rows.append(dict(fixture=name, codec=label, logicalBytes=len(data),
                             hybridRoutes=[select(block) for block in blocks] if label == "prototype-hybrid" else [],
                             hybridModes=[block[8] for block in stored] if label == "prototype-hybrid" else [],
                             storedBytes=sum(map(len, stored)), blocks=len(blocks),
                             structuredBlocks=sum((b[8] == 3 if label == "prototype-structure" else b[0] == 1) for b in stored) if label in ("prototype-structure", "structure-zlib-6") else 0,
                             sha256=hashlib.sha256(data).hexdigest(),
                             encodeMedianMs=statistics.median(encoding),
                             decodeMedianMs=statistics.median(decoding), roundtrip=True))
    report = dict(schemaVersion=3, recordedUtc=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
                  python=platform.python_version(), platform=platform.platform(),
                  codecSha256=hashlib.sha256(Path(__file__).with_name('codec.py').read_bytes()).hexdigest(),
                  entropySha256=hashlib.sha256(Path(__file__).with_name('entropy.py').read_bytes()).hexdigest(),
                  structureSha256=hashlib.sha256(Path(__file__).with_name('structure.py').read_bytes()).hexdigest(),
                  hexlanesSha256=hashlib.sha256(Path(__file__).with_name('hexlanes.py').read_bytes()).hexdigest(),
                  selectionSha256=hashlib.sha256(Path(__file__).with_name('selection.py').read_bytes()).hexdigest(),
                  benchmarkSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  worktreeBaseCommit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
                  worktreeDirty=bool(subprocess.check_output(['git', 'status', '--porcelain'], text=True).strip()),
                  iterations=5, zstandardReference=zstd_version, measurements=rows,
                  limitations=['Generated fixtures; no real project or network measurements.',
                               'Python prototype versus native zlib is not a fair runtime comparison.',
                               'Prototype includes 81 bytes per block; zlib includes its stream wrapper.',
                               'No archive/file metadata or production concurrency is measured.',
                               'Structural mode includes its complete inner frame and transform metadata.',
                               'Structure-zlib control includes transform metadata and a one-byte selector.',
                               'Hybrid selection samples at most 8192 bytes and tries only one transform family.',
                               'Hex-zlib includes transform metadata and a one-byte selector.',
                               'The numeric fixtures favor this transform; no universal advantage or novelty is established.'])
    write_text_exclusive(options.output, json.dumps(report, indent=2) + '\n')


if __name__ == '__main__':
    run()
