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
   entropy coder. Canonical Huffman is implemented in the second Python iteration;
   rANS remains a candidate. Deterministic output and an independent decoder are
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

### Second iteration: canonical Huffman — 2026-10-14

`encode_entropy` optionally compresses the same LZ77 tokens using the original
canonical Huffman implementation in `entropy.py`. The original `encode` path
remains the LZ77 baseline. The writer chooses the smallest of raw, LZ tokens or
entropy-coded tokens, including the 264-byte entropy header/codebook. Draft
mode 2 is understood only by the experimental reader; it is not a production
codec registration or stable format commitment.

The token stream is bounded at 132,096 bytes, code lengths at 24 bits; empty,
single-symbol and full canonical tables have explicit handling. Decoder checks
complete/non-oversubscribed tables, exact bit consumption, zero padding and
decoded token size before the existing LZ/digest checks. Hostile entropy payloads
with valid outer hashes still cannot bypass token distances or plaintext hashes.
The bit-by-bit Python decoder is deliberately simple and bounded; it is slow.

Generated YAML and source-code frames are respectively 28.4% and 33.3% smaller
than the first prototype. They remain larger than zlib level 6, and restores
are slower. This is research progress, not a promotion gate. See
[second-iteration evidence](release-evidence/1.9-compression-entropy-2026-10-14.md).
Next work: bounded deeper match search and a separately implemented native
reader with differential tests; measure native restore throughput before rollout.

## Third iteration: numeric context lanes — 2026-10-15

`encode_structured` adds a reversible numeric preconditioner before the existing
LZ77/Huffman backend. At most 32 lanes are selected from the preceding 64 bytes;
lexeme widths preserve zeros and literal bytes preserve signs/decimal punctuation,
NULs and line endings. Policies extract 1–18 or 4–18 digits. The writer compares
both complete candidates, including the transform and inner/outer frames, with
raw/LZ/Huffman fallback. Mode 3 is disposable; its reader forbids recursive
structure and checks all lengths, lane consumption, varints and hashes.

The new pipeline reduces generated numeric JSON frames from 75,775 to 6,413
bytes, but these regular counters favor delta coding. Ordinary generated YAML
and source improve by 15.2%/7.5% versus prior Huffman; native Zstandard remains
smaller/faster there. The same transform plus zlib 6 is still smaller on numeric
JSON than the custom backend. Full-size blocks whose transform exceeds 128 KiB
are skipped; speculative encoding has a CPU cost. This is measured research,
not a production codec or a novelty claim. Structure/value separation and
specialized value compression have prior art in
[XMill (2000)](https://sigmodrecord.org/publications/sigmodRecord/0006/pdfs/XMILL_%20An%20Efficient%20Compressor%20for%20XML%20Data..pdf).

See [third-iteration evidence](release-evidence/1.9-compression-structure-2026-10-15.md)
for reference versions, exact sizes, controls and reader limits. Next: held-out
corpus, cheap selection, bounded transform segmentation and independent native
reader qualification. A unique useful combination must be demonstrated rather
than inferred from implementing established primitives ourselves.

## Fourth iteration: bounded hybrid selection — 2026-10-15

`encode_hybrid` samples up to 8,192 bytes in four separated windows (or the
whole small block), selecting one transform family from content. Two sampled
standalone 32-digit hex lexemes choose hex packing; otherwise mostly printable
text with at least eight numeric runs chooses numeric lanes. Other blocks
retain the original raw/LZ/Huffman path. Extensions are not consulted. The
sampler bounds classification work, not total encoder time: full baseline
encoding and at most two numeric candidates or one hex candidate still run.
Thresholds are experimental and sampling can miss useful structure.

`hexlanes.py` packs standalone 32-digit hex spellings into 16 bytes, retaining
lower/upper case through tags and mixed case through a 32-bit mask. Literal
NULs are escaped, all other bytes remain exact, and no semantic GUID parsing or
schema normalization occurs. Contiguous longer hex strings are not split into
GUIDs. Disposable mode 4 holds one inner raw/LZ/Huffman frame; nested transforms
are refused. The reader checks size/expansion, escape tags, exact consumption,
canonical casing and existing inner/outer content hashes.

The writer compares complete candidate cost against the original baseline,
so a transform cannot enlarge that baseline's framed output. It can be worse
than the exhaustive structural profile by selecting a different family or
missing numeric structure. No incompressible block is skipped solely because
of a sample; raw fallback still comes from actual encoded cost. Hashes detect
corruption and do not establish authenticity. No product format, ZIP behavior,
updater schema or release gate changes.

See [hybrid measurements and limitations](release-evidence/1.9-compression-hybrid-2026-10-15.md).
This is an implemented hybrid research policy, not evidence of a new compression
primitive or worldwide novelty. Held-out licensed projects, independent native
readers, fuzzing and measured production safety/performance remain open.

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

## Research evidence safety

BUG-19034/#810 keeps public synthetic benchmark seeds unchanged for reproducible
corpus comparisons. Their PRNG calls do not generate production IDs, credentials
or encryption material. Three line-scoped Sonar exceptions document only those
non-security calls; product randomness is not exempted. Existing codec test
execution is measured with `coverage --source=scripts,experiments`, retaining the
same quality thresholds and production/test scope. Python and native benchmark
reports require a fresh filename and do not overwrite prior evidence. Operator
output directories remain an explicit local CLI choice, not a remote input API.
