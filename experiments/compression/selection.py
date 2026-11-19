"""Bounded content hints for disposable encoding experiments, never a decoder."""
import re
from experiments.compression.hexlanes import LEXEMES

SAMPLE_WINDOW = 2048
SAMPLE_WINDOWS = 4
MAX_BYTES = 128 * 1024


def select(data):
    """Choose at most one transform family from <=8192 sampled bytes.

    Missing a useful transform affects size only. The writer still compares full
    framed cost against raw/LZ/Huffman; no extension or sample controls decoding.
    """
    if len(data) > MAX_BYTES:
        raise ValueError('Input exceeds research block limit')
    if len(data) <= SAMPLE_WINDOW * SAMPLE_WINDOWS:
        samples = [data]
    else:
        span = len(data) - SAMPLE_WINDOW
        starts = [span * index // (SAMPLE_WINDOWS - 1) for index in range(SAMPLE_WINDOWS)]
        samples = [data[start:start + SAMPLE_WINDOW] for start in starts]
    size = sum(map(len, samples))
    if not size:
        return 'baseline'
    # Avoid joining distant windows: invented adjacency must not create lexemes.
    if sum(sum(1 for _ in LEXEMES.finditer(sample)) for sample in samples) >= 2:
        return 'hex'
    printable = sum(sum(byte in (9, 10, 13) or 32 <= byte < 127 for byte in sample) for sample in samples)
    numbers = sum(sum(1 for _ in re.finditer(rb'(?<![0-9])[0-9]{1,18}(?![0-9])', sample)) for sample in samples)
    if printable * 100 >= size * 90 and numbers >= 8:
        return 'numeric'
    return 'baseline'
