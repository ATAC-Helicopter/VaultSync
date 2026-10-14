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
from experiments.compression.codec import MAX_BLOCK_BYTES, decode, encode, encode_entropy


def run():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    options = parser.parse_args()
    rng = random.Random(1996)
    fixtures = {
        'unity-yaml': b''.join(
            f'--- !u!114 &{i}\nMonoBehaviour:\n  m_Name: Vehicle{i}\n  m_Enabled: 1\n  speed: {i % 200}\n'.encode()
            for i in range(6000)),
        'source-code': b''.join(
            f'public class Vehicle{i} {{ public int Speed => {i % 200}; }}\n'.encode()
            for i in range(8000)),
        'random-proxy': rng.randbytes(512 * 1024),
    }
    fixtures['already-deflated'] = zlib.compress(fixtures['unity-yaml'], 6)
    rows = []
    for name, data in fixtures.items():
        blocks = [data[i:i + MAX_BLOCK_BYTES] for i in range(0, len(data), MAX_BLOCK_BYTES)]
        for label, compressor, decompressor in (
                ('prototype-v0', encode, decode),
                ('prototype-huffman', encode_entropy, decode),
                ('zlib-1', lambda b: zlib.compress(b, 1), zlib.decompress),
                ('zlib-6', lambda b: zlib.compress(b, 6), zlib.decompress),
                ('zlib-9', lambda b: zlib.compress(b, 9), zlib.decompress)):
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
                             storedBytes=sum(map(len, stored)), blocks=len(blocks),
                             sha256=hashlib.sha256(data).hexdigest(),
                             encodeMedianMs=statistics.median(encoding),
                             decodeMedianMs=statistics.median(decoding), roundtrip=True))
    report = dict(schemaVersion=1, recordedUtc=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
                  python=platform.python_version(), platform=platform.platform(),
                  codecSha256=hashlib.sha256(Path(__file__).with_name('codec.py').read_bytes()).hexdigest(),
                  entropySha256=hashlib.sha256(Path(__file__).with_name('entropy.py').read_bytes()).hexdigest(),
                  benchmarkSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  worktreeBaseCommit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
                  worktreeDirty=bool(subprocess.check_output(['git', 'status', '--porcelain'], text=True).strip()),
                  iterations=5, measurements=rows,
                  limitations=['Generated fixtures; no real project or network measurements.',
                               'Python prototype versus native zlib is not a fair runtime comparison.',
                               'Prototype includes 81 bytes per block; zlib includes its stream wrapper.',
                               'No archive/file metadata or production concurrency is measured.'])
    options.output.parent.mkdir(parents=True, exist_ok=True)
    options.output.write_text(json.dumps(report, indent=2) + '\n')


if __name__ == '__main__':
    run()
