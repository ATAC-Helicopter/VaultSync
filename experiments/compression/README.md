# Compression experiment

Disposable research, excluded from product builds. See
[design and migration gates](../../docs/COMPRESSION_EVOLUTION.md).

From the repository root:

```bash
python3 -m unittest discover -s tests/scripts -p test_compression_prototype.py -q
python3 experiments/compression/benchmark.py --output /tmp/compression-prototype.json
```

The benchmark generates fixtures and writes a report. It never reads backups or
user project files. Draft frames are not a supported backup/container format.

The profile compares the original LZ77 encoder with `encode_entropy`, which
adds canonical Huffman coding and keeps raw/LZ fallback. Run all compression
tests with `-p 'test_compression*.py'`. Both paths remain isolated research.
