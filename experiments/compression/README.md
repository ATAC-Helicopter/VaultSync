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
