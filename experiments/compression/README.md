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


`encode_structured` tries bounded byte-exact numeric context lanes (mode 3), with
both broad and wide-only digit extraction and original Huffman/raw fallback.
No schema parsing, newline normalization or external dictionary is required.
Its decoder permits only one transform layer. Full transforms exceeding 128 KiB
are skipped. CPU costs and corpus-specific limits are recorded in the evolution
contract; synthetic counter gains are not universal compression improvements.

Optional external Zstandard reference (research environment only):

```bash
python3 -m venv /tmp/vaultsync-compression-reference
/tmp/vaultsync-compression-reference/bin/python -m pip install zstandard==0.25.0
/tmp/vaultsync-compression-reference/bin/python experiments/compression/benchmark.py --zstandard --output /tmp/compression-reference.json
```

Without `--zstandard` the benchmark needs only Python's standard library. The
structure+zlib control includes its transform metadata and one-byte selector;
all other prototype framing is included in reported byte counts.

`encode_hybrid` samples at most 8 KiB per block, selecting either numeric lanes,
32-digit hex packing or the original baseline. Hex packing preserves lowercase,
uppercase and mixed case plus arbitrary literal bytes. Experimental mode 4
stores the transform explicitly; decoding never repeats sampling. Complete
inner/outer frame costs decide whether the candidate wins. Numeric lanes keep
two bounded policies; hex packing tries one. Sampling may miss useful structure,
so this profile can lose compression opportunities versus exhaustive selection.
The benchmark includes a hex+zlib control and records hybrid routes/modes.
