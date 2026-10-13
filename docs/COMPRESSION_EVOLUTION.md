# Compression evolution

Owner: VS-1996, [issue #786](https://github.com/ATAC-Helicopter/VaultSync/issues/786),
1.9.0 preparation PR #736. Maintainer direction on 2026-10-13: improve the
existing compressor and develop a VaultSync algorithm gradually. This document
defines the first research iteration and proposed promotion gates. It does not
approve a new production format or promise its integration in the beta.

## Current backup behavior

File archives remain standard ZIP/DEFLATE, optionally encrypted after compression.
Existing archives are not rewritten. New installs still default to compression
off. When archive compression is enabled, known compressed media/containers use
Store, known text/code uses Optimal, and other extensions use Fastest.

The first measured change adds `.unity`, `.prefab` and `.meta` to Optimal,
including mixed-case extensions. Binary `.asset` retains Fastest. Legacy Unity
binary scenes/prefabs can cost more CPU under this extension policy; correctness
does not depend on text content. A bounded content classifier requires separate
representative measurements before replacing the extension policy. ZIP restore,
encryption, archive publication and cancellation semantics remain the same.

The generated profile compares whole ZIP bytes and verifies every entry after
every iteration. It is evidence about these fixtures on one machine, not a
prediction for Endless Drive, server transfers or all Unity projects. See the
[first measurements](release-evidence/1.9-compression-2026-10-13.md).

## Proposed VaultSync pipeline

1. Divide input into bounded, independently decodable blocks. Investigate block
   sizes and small-file grouping against actual restore and metadata overhead.
2. Match repeated sequences with an LZ dictionary. Keep lookup/search budgets
   explicit; compare a single candidate with bounded deeper searches.
3. Encode literals, match lengths and distances with a separately specified
   entropy coder. Compare Huffman and rANS candidates; neither is implemented
   in the first prototype. Deterministic output and an independent decoder are
   prerequisites for comparison.
4. Store the original block when encoding offers no saving. Include framing
   overhead in the decision at production container level. Sampling can avoid
   wasted work on incompressible input only after false positives are measured.
5. Hash decoded content, bind encoded bytes and metadata, then encrypt using
   the owning authenticated encryption contract. Compression precedes encryption.
6. Schedule bounded parallel encoding with an ordered manifest, backpressure,
   cancellation and total memory limits. Benchmark disk/network bottlenecks and
   restore speed as well as encoder throughput.

Compression and cross-backup deduplication have separate correctness contracts.
Do not claim space saved by excluded caches, incremental selection or object
reuse as a codec improvement. DEFLATE already combines LZ77 and Huffman coding
([RFC 1951](https://www.rfc-editor.org/info/rfc1951/)); an original implementation
still needs established algorithms and independent interoperability tests.
Zstandard is an external benchmark/reference candidate, including its dictionary
facilities ([upstream](https://github.com/facebook/zstd)). It is not introduced
as a runtime dependency in this iteration.

## Implemented experiment v0

`experiments/compression/codec.py` is a dependency-free Python LZ77 experiment.
It uses at most 128 KiB input per frame, a 65,535-byte backward window, a bounded
16,384-slot match table and one candidate per lookup. Literal runs are 1–128
bytes; matches are 4–131 bytes with a two-byte distance. Blocks have no shared
history. The encoder selects raw storage when token payload is not smaller.
Skipped match positions are not indexed; this is a research tradeoff, not an
optimal parse. There is no entropy coding, concurrency, resumability or archive
file/directory metadata yet.

Draft framing adds 81 bytes per block: draft magic, mode, logical/stored lengths,
SHA-256 of plaintext and SHA-256 binding the header to encoded bytes. The decoder
checks frame/length limits, exact consumption, tokens, distances, output bounds
and both hashes. These unkeyed checks detect corruption; they do not authenticate
an attacker-controlled frame. Future archive readers must verify trusted manifest
or authenticated encryption evidence and enforce a total decoded-byte budget
before publication. Per-block limits alone cannot bound an untrusted archive.

The prototype is outside solution projects and packaging. It only transforms
in-memory bytes, has no production registration or stable codec identifier, and
cannot capture or restore backups. Its magic/layout is disposable research data.
Never write these frames into disk images, ZIP files or BSC repositories.
Tests include boundary/random roundtrips, raw fallback, a hand-written overlap
fixture, malformed tokens with valid encoded checksums, corrupt headers,
truncation, trailing bytes and logical digest mismatch. They are not fuzzing or
independent implementation qualification.

## Incremental rollout and acceptance

| Stage | Deliverable | Promotion evidence |
|---|---|---|
| 0: baseline | Measured ZIP tuning and disposable Python prototype | Exact readback, failure/cancellation regressions, traceable reports |
| 1: algorithm | Entropy coding, bounded match search and block-size experiments | Representative corpus; compare ZIP and Zstandard with equivalent metadata |
| 2: native implementation | C# encoder and independently written decoder | Golden fixtures, differential fuzzing, malformed data and resource budgets |
| 3: shadow | Optional research encoding while ZIP is authoritative | No change to published backup artifacts; measured CPU/RAM and byte equality |
| 4: opt-in writer | Reviewed container contract and explicit project opt-in | Readers delivered first; recovery/updater compatibility and all target OS tests |
| 5: default decision | Broader opt-in evidence and reviewed migration | Demonstrated useful size/speed tradeoff and safe rollback |

There are no promised dates for stages 1–5. Each stage must demonstrate value;
if the algorithm does not beat mature codecs in a useful profile, retain ZIP
or use a reviewed mature codec rather than promote a worse format.

Proposed promotion targets, subject to representative baseline review: at least
10% smaller complete artifacts on the target mixed corpus at no more than twice
ZIP Optimal encoding time, no more than 10% slower restores, and an explicitly
reviewed peak memory ceiling. Fast and dense profiles may have different budgets.
These are planning targets, not satisfied gates or universal performance claims.
Compare Release native implementations on identical fixtures, metadata, machines
and concurrency. Measure logical/stored bytes, small-file overhead, median/tail
throughput, restore, CPU, native+managed peak memory, cancellation and storage I/O.
Python/native speed comparisons do not establish these targets.

Before any new production writer: unknown versions fail closed; no overwrite of
known good data; bounded total output; corruption, source changes, disk-full,
crashes and interrupted publication tested; compressed then authenticated
encryption verified; old archives remain readable; Windows/macOS/Linux restore
qualification and installed updater behavior established. Publish only after
durable completion and readback under the owning backup contract. A smaller codec
output is not itself a successful or recoverable backup.

Readers must ship before writers. Opt-in applies only to newly created backups;
do not destructively migrate old archives. Keep ZIP reading supported. Rollback
disables new writers while preserving readers for committed new artifacts.
Any ZIP fallback must fit available workspace capacity before creation; never
silently relabel an experimental payload as ZIP.

## Release ownership

VS-1996 in 1.9.0 owns compatible ZIP tuning, this design and isolated experiments.
The beta may test ZIP tuning; it must not advertise a custom production codec.
Disk transform/version descriptors stay under VS-1917/#574. Stable BSC repository
architecture and formats remain VS-1981/#703 in 1.9.6. Any integration affecting
those formats must use their owning contracts and preparation branches. This
design does not import BSC features into the 1.9.0 binary.
