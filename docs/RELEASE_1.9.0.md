# VaultSync 1.9.0 — Disk and Bootable Recovery Foundation

Planning release; current stable is 1.8.9. Stable target: 2027-03-26.
Active development branch: `work/1.9-roadmap-realignment`; sole release PR:
[#736](https://github.com/ATAC-Helicopter/VaultSync/pull/736) into `Dev`.
Protected release identity: `release/1.9.0`; its older ref is not the assembled
working tree. Promotion to `Stable` uses reviewed merge commits.
No disk support or predecessor upgrade is qualified yet.

VS-1980/#673 keeps development diaries, Reddit drafts and publication packages
in a private editorial workspace outside GitHub. Existing public commit history
is preserved; manuscript files are removed from the current preparation tree.
Technical release evidence remains in its owning documentation. The new diary
is [published on FG Labs](https://fglabs.dev/devlog/vaultsync-190-following-the-files)
from the private site source. Website issue #25/PR #43 owns automatic product
notifications, with provider-confirmed delivery of the new article and duplicate
suppression. This website behavior does not qualify the VaultSync binary or beta.

VS-1997/#787 requires the October security-serviced .NET 10.0.13 runtime,
SDK 10.0.402 and coordinated Microsoft packages. Release runtime audits verify
every included framework, including optional ASP.NET/Windows Desktop, instead
of accepting an updated NETCore beside vulnerable companions. See
[runtime security](RUNTIME_SECURITY.md) and
[current servicing evidence](release-evidence/1.9-runtime-servicing-2026-10-14.md).
This does not qualify installed upgrades, recovery or default-branch alert closure.
The October 15 all-branch follow-up rechecks all nine serviced preparations and
prepares 17 scoped security backports, including protected Stable/Dev and release
identities. Protected integration and the 18 default alerts remain open; see
[branch-specific evidence](release-evidence/1.9-runtime-branches-2026-10-15.md).

VS-1996/#786 adds measured ZIP tuning for Unity scenes, prefabs and metadata,
plus an isolated bounded LZ77 compression experiment. Production backups remain
ZIP; no custom codec is registered, shipped or promised for beta. The experiment
currently compresses less densely than DEFLATE. Reader-first opt-in migration
and measured promotion gates are proposed in
[compression evolution](COMPRESSION_EVOLUTION.md); source-qualified measurements
and current validation are in
[compression evidence](release-evidence/1.9-compression-2026-10-13.md).
The October 14 research iteration adds bounded canonical Huffman coding, reducing
generated frames by 28.4%/33.3% against v0 with slower Python decode. It remains
outside product builds and does not outperform DEFLATE overall. See
[entropy iteration evidence](release-evidence/1.9-compression-entropy-2026-10-14.md).

The October 15 research iteration adds byte-exact numeric context lanes and
cost-based selection, with original raw/Huffman fallback. Synthetic numeric JSON
frames shrink 91.5% against prior Huffman; ordinary YAML/code improve 15.2%/7.5%
but remain behind Zstandard. This is corpus-specific experimental progress;
no new application codec or verified novelty is established. See
[structural compression evidence](release-evidence/1.9-compression-structure-2026-10-15.md).

VS-1995/#784 makes existing file-backup metadata imports visible in Projects,
including entries with no usable source path. A local persisted import marker
drives translated badges in cards, compact rows and details; restoring changes
source availability without erasing that marker. Legacy inferred origin requires
imported-only history and an unavailable source. This extends the current file
backup UI, without moving the separate 1.9.2 portable recovery scope forward.
Missing imported sources disable snapshot/source-folder actions. Local Release
verification passed with 0 warnings/errors, 1,177 Core and 154 Python tests;
the 19 locale files contain the four new labels. Focused native macOS rendering
was visually inspected in an isolated profile. See
[imported project evidence](release-evidence/1.9-imported-projects-2026-10-13.md).

BUG-19029/#783 corrects macOS SMB cleanup for destinations inside a share.
Auto-unmount resolves a current SMB mount root and checks path containment;
similar path prefixes cannot select a neighboring mount. The audit reproduced
the nested-path failure independently of exclusion filtering.
Required local Release checks on this correction passed with 0 warnings/errors,
1,170 Core and 154 Python tests. Native macOS cleanup of the audit-owned SMB
mount succeeded; the mount table confirmed it was absent afterward.

BUG-19028/#782 adds saved Unity `.pdata` profiling captures to the Unity preset's
exclusions. Read-only archive inspection found those recordings dominated a real
source backup; normal game assets, source, settings and package locks are retained
by regression fixtures. Existing archives are preserved. See
[the bounded preset audit](release-evidence/1.9-exclusion-presets-2026-10-13.md).

BUG-19027/#781 fixes lowercase preset catalog fields being ignored by UI, CLI
and backup readers. Indexed IDs now display and resolve to their declared rule
files; legacy PascalCase catalogs remain supported. A cross-layer regression
checks catalog selection, mapped content and actual file exclusion.
Local Release checks on the catalog fix passed: 0 build warnings/errors,
1,163 Core tests and 154 Python tests. Hosted CI remains separate.

BUG-19026/#780 rejects unaligned raw payload offsets before byte verification;
512/4096-sector regression cases cover byte ranges that otherwise fit the part.
This closes a map-validation defect without qualifying disk access or restore.
Local Release validation on the BUG-19026 implementation passed with no build
warnings/errors, 1,151 Core tests and 154 Python tests. Six new cases cover
unaligned refusal before reads and preserved aligned offsets. Hosted CI remains
separate; Project #7 retains In progress, Start 2026-10-13, Target 2027-03-26.

## Delivery order

The maintainer reaffirmed the complete-feature beta priority on October 15.
Compression research (VS-1996) is secondary and does not block that beta. The
VS-1905 pure overwrite planner now binds operation/image/destination observations
and invalidates changed selections, without device I/O or write authorization.
This is a prerequisite increment, not the completed restore workflow. See
[current readiness](release-evidence/1.9-beta-readiness-2026-10-15.md) and
[preview evidence](release-evidence/1.9-disk-preview-2026-10-15.md).

1. Finalize the concrete route, serialization and privilege/security boundaries
   under the joint direction approved on 2026-10-20 for VS-1910/1917/1918/1919.
   Reuse the retained approval; support remains individually qualification-gated.
2. Implement VS-1904 capture and VS-1905 exact source/destination and wipe
   previews; incomplete/corrupt captures fail explicitly.
3. Implement VS-1906 independent boot media and VS-1907 image-to-disk restore.
4. Record VS-1908 evidence and qualify VS-1909 Capture → Validate → Boot →
   Restore → Validate on the declared hardware/VM/filesystem/encryption matrix.
5. Qualify exact 1.8.9 upgrades, distribution integrity and user-data preservation.

Unsupported Secure Boot, live capture, encryption and filesystem combinations
must be explicit. If the complete loop fails, imaging remains Preview.
There is no universal bare-metal recovery claim. BSC prerequisites do not
block this release. Concrete route/API and format/security finalization remain open under the
approved joint direction.

The [VS-1917 image-format review draft](DISK_IMAGE_FORMAT_1.9.md) now specifies
logical coverage, integrity, completion/checkpoints, unknown-version rejection,
independent inspection and a permanent fixture plan. A bounded draft header
recognizer and serialized refusal fixtures are implemented; no complete-image
reader/writer or approved serialization exists. A bounded draft manifest and
completion reader now binds raw-part SHA-256 digests to image/capture identity,
generation and logical coverage without disk writes. Draft schema 2 now binds regular-file source/tool claims and GPT layout
observations. Native source consistency, authenticated provenance and approved
complete-image serialization remain open. VS-1904 now has a synthetic file capture prototype with
ordered file flushes and explicit incomplete/interrupted/failed results;
crash/filesystem durability and device capture remain unqualified.
VS-1907 reconstructs synthetic raw/zero maps into a new temporary file from
payloads staged during verification and checks target read-back. This file loop
does not qualify the independently booted restore path or a physical destination.
VS-1904 also has a separate development-only read-only inventory executable,
with a bounded one-shot protocol and deadline. It has no launcher integration,
authentication, elevation, device access or release packaging. See
[worker contract](DISK_WORKER_1.9.md) and
[current evidence](release-evidence/1.9-disk-worker-2026-10-17.md).
Engine/support decisions (VS-1918), shared
identity/evidence alignment (VS-1919) and fixtures remain open under the
approved joint direction.
The [VS-1919 identity/evidence review draft](RECOVERY_IDENTITIES_1.9.md) defines
generation binding, dependency/failure correlation, evidence provenance and
freshness, privacy and read-only portable inspection. A single-observation
applicability prototype has synthetic fixtures; stable schemas, dependency
evaluation and integration remain open. Joint implementation direction was
approved on 2026-10-20; concrete security/format work and qualification remain open.
The [VS-1918 engine ADR](adr/001-imaging-engine-1.9.md) proposes an internal
raw/offline worker and explicit support exclusions. The executable
[qualification matrix](../release/disk-support-1.9.0.json) has four candidates
and zero qualified profiles. `python3 scripts/disk_support.py check` verifies
structural evidence rules; `--require-qualified` intentionally fails until actual
approved, measured recovery evidence exists. This adds no engine or device access.
Qualification runs must begin at or after valid retained engine approval; an
approval recorded after capture cannot qualify that earlier run.
Local architecture/workflow progress and its verification limits are recorded
in [the October 4 ledger](release-evidence/1.9-work-2026-10-04.md).

VS-1998/#805 adds optional allowlisted technical diagnostics, device-local opt-in,
bounded local/queued retention, cancellable receipt-checked HTTPS delivery and a
companion private FG Labs receiver (fglabs-site#49). Raw logs and text crash reports
remain manual. Provider/production activation is separately configured; see
[the contract](PRODUCT_DIAGNOSTICS.md) and
[current validation](release-evidence/1.9-product-diagnostics-2026-10-19.md).
The maintainer requested site main integration; a synthetic hang alert reached
its operator inbox using the configured Production sender. This does not qualify
imaging, a beta or delivery of every future report.

## Scope realignment on 2026-10-03

The future CLI rework is preserved in the 1.9.5 branch; discovery and completion
are adapted to the supported flat CLI in 1.9.1. BUG-19001 remains an independent
safety fix. VS-1980 retains kickoff ownership. Updater organization-transfer
compatibility and AppImage packaging fixes remain maintenance prerequisites.
Windows notification and xUnit modernization are separately tracked maintenance
work, not evidence that disk recovery is ready.

See [family ownership, gates and ID crosswalk](RELEASE_FAMILY_1.9.md),
[canonical roadmap](../ROADMAP.md) and [route draft](ROUTE_ARCHITECTURE_1.9.md).

Legacy watcher safety is retained: finite read-only text dry runs, explicit `--db`,
shutdown cancellation/drain and explicit failure. Resource/JSON commands stay in 1.9.5.

Raw command arguments are not logged, preserving secret input safety.

BUG-19009 requires CI/CodeQL/Sonar triggers to include documentation changes:
protected PR contexts must exist and bundled CLI documentation affects artifacts.

## Assembled release review — 2026-10-03

PR #736 into Dev is the sole 1.9.0 release review, including its maintenance,
contracts and tracking. Its preparation source is work/1.9-roadmap-realignment;
#683 is superseded. It stays draft and is not eligible for automatic promotion.
BUG-19010/#749 and BUG-19011/#750 qualify dependency ABI/API coherence.
The unused Inter package is removed without changing bundled/system fallbacks.
See [the complete daily ledger](release-evidence/1.9-work-2026-10-03.md).

## Linux startup qualification

BUG-19012/#751 tracks the installed Linux process blocked during native font
initialization. Relaunch passes initialization but window visibility, triggering
input and durable remediation remain open. See [the evidence](release-evidence/linux-startup-2026-10-04.md).
A living process or completed startup log is insufficient desktop qualification.

## Repository tracking — 2026-10-07

VS-1980/#673 distributes the shared helper from #759/#758. Use `./dev adopt` for existing owning issues and `./dev status` before pushes; reuse this release's preparation PR. Allocating IDs requires a complete remote audit. This tooling does not qualify product or release gates.

The October 15 reconciliation adds `./dev audit --release 1.9.0` as a tracked
push prerequisite and `--all-releases` for the full family. All 41 release IDs
already had issues and Project entries, but 34 had inconsistent/missing metadata;
these were repaired without closing release-gated work. One missing conditional
1.9.4 record is disposed separately as not planned with historical proof.
See [tracking evidence](release-evidence/1.9-tracking-audit-2026-10-15.md).
BUG-19033/#792 also prevents default adoption from adding conflicting P2 labels
to established P0/P1 work; it preserves canonical priority and fails on denied
metadata updates. This is shared contributor tooling, not a new binary feature.

## Route adapter prototype — 2026-10-07

VS-1910/#501 adds a pure typed-route mapping prototype and compatibility/refusal tests, as preparation for the proposed route API review. It does not replace current pages, saved state or navigation commands. See [route contract](ROUTE_ARCHITECTURE_1.9.md); architecture approval and platform UI parity remain open.

VS-1918 qualification input hardening (2026-10-09) rejects ambiguous/non-finite/non-UTF-8 JSON and bounds actual reads against growing files. Four candidate profiles remain unqualified; evidence parsing does not approve an engine or prove recovery.

## Navigation and observation prototypes — 2026-10-09

VS-1910/#501 adds a page-only resolve-before-commit coordinator with bounded
back/forward history and explicit cancellation/supersession. VS-1919/#576 adds
a read-only single-observation applicability classifier, separating provenance,
binding, chronology, outcome and measurement basis. Both remain review prototypes
without UI, persistence or device integration. The existing confidence service
also fixes future-dated verification/drill results incorrectly being treated as fresh.
See [current development evidence](release-evidence/1.9-prototypes-2026-10-09.md).
Joint architecture approval, platform parity and measured recovery gates remain open.

VS-1918/#575 CI follow-up (2026-10-09) replaces interpreter-dependent nesting
refusal with an explicit 64-container bound for JSON qualification input.
See [repair evidence](release-evidence/1.9-json-depth-2026-10-09.md).

## First visible page-navigation increment — 2026-10-09

VS-1910/#501 subsequently connects the page-only route coordinator to the legacy
shell, with localized Back/Forward and Alt+Left/Right. Cached page state remains
in process; startup restoration, last-page write ordering and same-snapshot
History draft preservation are corrected. No resource route, disk workflow or
new persisted authorization is introduced. The earlier prototype snapshots
above remain historical. See [current UI evidence](release-evidence/1.9-navigation-2026-10-09.md)
and [navigation contract](ROUTE_ARCHITECTURE_1.9.md). Full parity, joint
architecture, predecessor upgrade and measured disk-recovery gates remain open.

## Development publication — 2026-10-09

The [navigation diary](https://fglabs.dev/devlog/vaultsync-190-keeping-your-place) is live with actual native screenshots and explicit
stable, fixture and recovery limits. Website PR #42 and its Production deployment
pass; Reddit copy is prepared for the maintainer to submit. The release remains
in draft PR #736, with Sonar, full desktop parity, architecture and measured
recovery gates open. Implementation test results refer to `42e6b064`, not to this
later documentation-only update.

## Windows contributor validation — 2026-10-12

VS-1980/#673 preserves the exact imported planning-source digest with a scoped
LF checkout rule. Updater orchestration fixtures can mock Unix process cleanup
on Windows. The symlink escape check is separate from ordinary artifact refusal
checks and skips only when Windows denies link creation. This does not qualify
an installed upgrade or authorize changes to the imported planning document.

## Dependency applicability prototype — 2026-10-12

VS-1919/#576 adds a bounded, read-only dependency graph over explicitly supplied
observations. Named requirements retain missing dependencies; alternatives do
not bypass required keys, unsupported evidence, cycles or graph limits. This is
synthetic architecture evidence, with no storage, secret or device access and no
overall recovery claim. Stable schemas, observation selection, failure-domain
scenarios and joint approval remain open. See [the current evidence](release-evidence/1.9-dependencies-2026-10-12.md).

## Compact changelog standard — 2026-10-12

VS-1980/#673 updates AGENTS.md and CONTRIBUTING.md to prefer one short sentence
of 8–14 words, with the existing 22-word ceiling. Consecutive entries have no
blank or whitespace-only separators; headings retain their spacing. Automated
checks cover both active and historical lists. Shared standards are distributed
to all nine preparation PRs; each retains its own unreleased scope and metadata.

## Failure-domain simulation prototype — 2026-10-12

VS-1919/#576 adds a bounded, generation-bound model for one explicit failure
domain. Shared disks/accounts remove their correlated alternatives together;
missing membership remains unknown. Every result is simulated and preserves
its supplied baseline observations. No domain discovery, device/secret access
or measured recovery qualification is introduced. Joint architecture approval
remains open. See [current scenario evidence](release-evidence/1.9-failure-scenarios-2026-10-12.md).

## Linked contract batch — 2026-10-12

VS-1917/#574 adds bounded in-memory image coverage checks; complete structure
remains unverified. VS-1919/#576 combines explicit simultaneous failures and
flags multiple current observations as ambiguous, retaining basis and historical
records. These prototypes have no encoded-image reader, disk I/O, producer trust
or automatic conflict precedence. Joint approval and measured disk/boot/restore
gates remain open. See [batch verification](release-evidence/1.9-contract-batch-2026-10-12.md).

## Defect ownership and tracking reliability — 2026-10-12

Active corrected defects now have independent BUG ownership; development IDs
and published historical entries remain unchanged. Retained watcher safety and
updater compatibility maintenance are categorized as Changed. Active Fixed
entries require one BUG identifier. Repository tracking explicitly decodes UTF-8
and reserves allocated IDs locally while remote listings catch up. The ten new
defect records remain open for release acceptance. See [ownership and verification](release-evidence/1.9-defect-ownership-2026-10-12.md).

## Desktop dependency simplification — 2026-10-12

VS-1994/#776 updates the five Avalonia packages to 12.1.4, Dapper to 2.1.89
and the xUnit runner to 4.0.1. Native Avalonia ring/bar drawing replaces
LiveCharts; localized legends, colors, sorting and free-space proportions remain.
The unused weekly chart construction and three resolved LiveCharts packages
are removed. Rendering/security overrides remain, including Avalonia's transitive
WebAssembly dependencies. Windows offscreen rendering is checked; Linux/macOS
installed desktop acceptance remains open. See [audit and evidence](release-evidence/1.9-desktop-dependencies-2026-10-12.md).

## macOS chart rendering — 2026-10-13

VS-1994/#776 now has a native macOS offscreen render of the actual chart
control: two segments, replacement with a full ring, replacement with an empty
ring, and a 65% bar. The serialized Release build and all 1,100 Core and 151
Python tests pass locally. This is focused rendering evidence, not installed
desktop or Linux qualification. See [the macOS record](release-evidence/1.9-desktop-macos-2026-10-13.md).

## Beta preparation — 2026-10-13

A near-term beta is the next delivery objective. The maintainer requires every
feature owned by 1.9.0, including a qualified disk recovery path, before beta.
The version suffix and date remain unset. The [readiness snapshot](release-evidence/1.9-beta-readiness-2026-10-13.md)
orders the architecture, measured recovery, desktop/upgrade and CI work without
changing the stable release gate or claiming disk support.

Beta means the complete 1.9.0 feature paths are integrated and ready for wider
testing, with no known data-loss or app-breaking defect. Before distributing a
beta that can write disks, measure the full five-stage loop on at least one
explicitly supported disposable virtual profile and pass wrong-destination,
corrupt-image and interruption refusals. List all unqualified combinations as
unsupported. Broader physical-device and platform qualification remains a
stable-release gate; the beta does not imply universal bare-metal support.
The 1.9.0 beta asset workflow enforces the qualified-profile matrix gate before
building packages. A passing structural check still requires review of the
retained measurements and all other release gates.

BUG-19023/#777 removes stale beta feature claims from the generated Linux
`.deb` and AppImage metadata. The package descriptions are release-neutral;
actual beta contents and qualification remain governed by the selected scope.
See [the bounded validation record](release-evidence/1.9-linux-package-metadata-2026-10-13.md).

BUG-19024/#778 separately tracks the macOS DMG builder's beta suffix in
numeric bundle-version fields. The implemented mapping uses `X.Y.Z` for the
short version and `X.Y.(1000*Z+N)` for beta `N` (1–998); the final build uses
`X.Y.(1000*Z+999)`. `VaultSyncReleaseVersion` preserves the exact suffixed
target. See [native synthetic DMG evidence](release-evidence/1.9-macos-beta-bundle-2026-10-13.md).
Actual beta packages and exact 1.8.9 upgrade behavior remain unqualified.

BUG-19025/#779 tracks the SonarQube Cloud PR lookup failure across the 1.9
preparation PRs. A successful branch analysis does not satisfy #736's PR
quality gate. See the [failure diagnosis](release-evidence/1.9-sonarqube-pr-2026-10-13.md);
the gate stays open until a current-head PR analysis passes. On October 19,
authenticated inspection found the organization unbound despite the project
showing BOUND. Maintainer-approved installation limited to VaultSync restored
the organization binding and exposed the correct repository. See
[the configuration follow-up](release-evidence/1.9-sonarqube-binding-2026-10-19.md)
for the rerun and its actual outcome.

BUG-19034/#810 owns findings exposed by the restored analysis on ec202c1:
four reliability/nine security findings and 62.2% new-code coverage. Navigation
explicitly opts out of the unrelated updater token; source arguments and proof
paths are validated before verification, retained outputs are created exclusively,
and the existing experimental tests now contribute measured coverage. Deterministic
public benchmark bytes are preserved with three reviewed call-site exceptions.
[Safety/coverage follow-up](release-evidence/1.9-sonar-safety-2026-10-19.md) records
exact source results and the hosted gate separately; thresholds remain unchanged.
Hosted 1a777b0 reached 83.6% coverage and zero reliability findings. Two public
synthetic benchmark PRNG findings were formally reviewed as false positives;
verifier options now bind values with `=`. The actual hosted gate on 6ee344e
passes after documented review of the source-ref false positives, verified
2026-10-20. Release-gated integration and beta qualification remain separate.

## Release signing — 2026-10-13

VS-1902/#113 requires full-inventory keyless provenance signatures and retained
offline proof, bound to the authorized source workflow/ref/commit and checked
again before draft upload. The updater format and asset inventory remain
unchanged. Native publisher signing remains financially gated and metadata
remains unsigned. Historical 1.8.9 cryptographic verification and tamper rejection
are separate from current-head tests and pending new-run/platform qualification.
See [shared signing policy](RELEASE_SIGNING.md). Refs #113; #736 remains draft.

Signing standard delivery (2026-10-13): the shared rule is committed and pushed
to all nine sole preparation PRs, with each real release's metadata preserved
and its required CLI Release checks passed. See [per-head signing evidence](release-evidence/1.9-signing-2026-10-13.md).
Current tests, historical signatures and pending publication gates remain separate.

## Localization coverage — 2026-10-13

BUG-19030/#785 repairs missing desktop keys, loading text, coverage labels, Recovery
summaries and distinct updater outcomes in all 19 locales. Guide/Recovery cached
presentation reapplies on language changes. The shared localization contract
requires resource coverage, placeholder parity, static-source checks, state
preservation and rendered review for every added UI string. See
[localization contract](LOCALIZATION.md). Refs #785 in draft #736; technical
historical payloads and remaining release qualification are separate.

Localization delivery (2026-10-13): all nine sole preparation PRs have the shared
rule and validated existing-desktop repairs, retaining release ownership and
metadata. Locale-only changes invalidate reused packages. See
[per-head localization evidence](release-evidence/1.9-localization-2026-10-13.md); native translation/rendering and
release qualification remain separate.

## Synthetic capture resource limit — 2026-10-14

BUG-19031/#789 refuses synthetic capture requests above 1 GiB before reading
input or creating a temporary workspace, matching the reconstruction fixture
limit. Sector-boundary refusal and exact-limit truncation tests use small inputs.
See [current verification](release-evidence/1.9-capture-limit-2026-10-14.md).
This adds no device access or measured disk qualification. Refs #789;
#736 remains draft.

## Complete destination sidebar totals — 2026-10-14

BUG-19032/#790 separates destination logical accounting from partial Backups
page caches. A complete narrow database projection builds totals and protected
cleanup suggestions off the UI thread, while cached pages retain their limits.
All locales describe known logical size; language changes reapply cached labels.
[Verification and tray diagnosis](release-evidence/1.9-destination-sidebar-2026-10-14.md)
keep synthetic checks, native review and actual physical allocation separate.
The installed Linux app still uses Avalonia 12.1.2; the development tree already
uses 12.1.4's D-Bus registration corrections under VS-1994/#776. A fresh login
with the updated app is not yet qualified. Refs #790; #736 remains draft.

## Reconstruction capacity admission — 2026-10-15

BUG-19031/#789 also refuses oversized reconstruction metadata before raw payload
reads or staging writes. Small 512/4096-sector regression inputs reproduce the
previous late refusal; exact-limit declarations still reach truncation checks.
This closes a resource-bound gap in the synthetic pipeline, without changing
the image layout or qualifying physical restore. See
[follow-up evidence](release-evidence/1.9-reconstruction-limit-2026-10-15.md).

## Secondary hybrid compression research — 2026-10-15

VS-1996/#786 adds an isolated content sampler and exact-case GUID-like hex
packing. Each block tries one transform family and keeps the smallest complete
result versus baseline. The draft decoder reads an explicit mode and rejects
nested transforms. This remains outside application builds; the beta continues
to use compatible ZIP. See [current research evidence](release-evidence/1.9-compression-hybrid-2026-10-15.md).

## GPT layout prerequisite and beta priority — 2026-10-15

The maintainer requests a complete-feature beta within the coming days. This is
an urgency goal, not a qualified release date: disk-worker, independently booted
recovery, native identity and end-to-end recovery gates remain unfinished.
Further custom-compression experiments are deferred while these blockers take
priority; compatible ZIP remains unchanged.

VS-1917/#574 adds bounded GPT inspection, independently generated 512/4096-sector
fixtures and a capture/reconstruction/layout readback exercise. It refuses corrupt
or conflicting tables and invalid/overlapping partitions without disk writes.
This prerequisite does not approve the pending architecture contracts or qualify
boot/recovery. See [GPT validation](release-evidence/1.9-gpt-inspection-2026-10-15.md)
and [remaining beta work](release-evidence/1.9-beta-readiness-2026-10-15.md).

## Disposable UEFI laboratory preflight — 2026-10-15

VS-1918/#575 records an isolated pinned Debian guest boot with UEFI/ext4/512
observations, normal shutdown and real-image GPT inspection. Cloud network/SSH
startup failures are retained; this is not the recovery product medium. No
engine approval or disk-support row changes. See
[measured lab inputs and limits](release-evidence/1.9-vm-lab-preflight-2026-10-15.md).

## Bound GPT fixture origins — 2026-10-15

VS-1917/#574 integrates source/observation IDs, UTC time, physical geometry,
GPT layout and tool version/source commit into provisional schema-2 manifests.
Explicit GPT-file capture checks the copied layout before completion; file
reconstruction refuses observations inconsistent with reconstructed bytes.
Schema 1 remains readable and the generic writer keeps its existing format.
Caller source/tool claims are not authenticated; this adds no device adapter,
boot medium or release qualification. See [current origin evidence](release-evidence/1.9-image-origin-2026-10-15.md).

## Linux block inventory — 2026-10-17

VS-1905/#493 adds bounded, read-only kernel metadata observations for disk and
partition relationships, geometry, identity ambiguity and current-namespace use.
Changed or incomplete scans return no devices. No result declares a disk Offline
or authorizes writes; native handle checks and the isolated worker remain open.
No application-visible string or file-backup behavior changes. See
[current checks and scope](release-evidence/1.9-linux-block-inventory-2026-10-17.md).

## Inventory parent transport — 2026-10-20

VS-1904/#492 adds the fixed adjacent-apphost consumer for the read-only worker.
It sends one correlated request and EOF, validates bounded strict reply data,
requires empty diagnostics and successful process exit, and bounds failure/timeout
cleanup. Neither image content nor UI input chooses executables or arguments.
Tests launch the real worker on each CI platform; native negative process probes
exercise refusals and cancellation. This closes the missing parent transport
boundary, not desktop/media integration, authentication, privilege or device
access. Current evidence is [the client record](release-evidence/1.9-disk-worker-client-2026-10-20.md).

## Joint implementation review candidate — 2026-10-20

VS-1918/#575 coordinates the concrete combined direction with VS-1910/#501,
VS-1917/#574 and VS-1919/#576 in [the review package](ARCHITECTURE_REVIEW_1.9.0.md#implementation-decision-package--2026-10-20).
It selects the recommendation, bounds and refusal/operation semantics for review,
with desktop locales and installed updater/provenance qualification preserved.
The earlier engine-choice delegation is acknowledged separately from the actual
joint maintainer approval required before device integration. Matrix decision
and all candidate status stay unchanged; no review or support is fabricated.

## Actual joint approval — 2026-10-20

After reviewing the combined package at 484be54, the maintainer explicitly
approved it. [The immutable review record](release-evidence/1.9-joint-architecture-approval-2026-10-20.md)
is hash-bound in the engine matrix; decision is now approved while all four
profiles remain candidates with no qualifying evidence. This advances the
architecture direction gate, preserving concrete implementation/security review,
disposable-VM scope, exact source/destination authorization and every measured
recovery, installed updater, signing and release gate. Prior laboratory runs
remain historical and cannot predate this approval for qualification purposes.

## Native held read descriptors — 2026-10-20

VS-1904/#492 adds the Linux x64 same-privilege read primitive. Fresh metadata
brackets a fixed kernel-node open; held descriptor checks bind block type,
device number, capacity, sectors, disk sequence and kernel read-only state.
Mounted/busy, changed, ambiguous, stale and uninspectable inputs refuse. Each
read rechecks the descriptor and closes after invalidation or I/O failure.
[Native virtual-device evidence](release-evidence/1.9-linux-block-read-2026-10-20.md)
includes a complete 128 MiB read, both sector geometries, exclusive-claim release
and live hot removal. This does not implement complete capture, authenticated
privilege, repository ancestry, write protection, restore, media or desktop UI.
The inventory worker remains metadata-only; no profile is qualified.


## Raw image profile reader — 2026-10-20

VS-1917/#574 fixes the [first reader wire contract](DISK_IMAGE_RAW_WIRE_1.9.md)
under the approved joint direction. Read-only verification checks bounded
metadata, completion/root, exact full raw coverage/digest and actual GPT against
its declared layout. Synthetic block claims and refusal fixtures cover 512/4096
geometries; source kind is not authenticated provenance. Generic historical
fixture/schema compatibility remains intact. No product image writer, durable
capture, privileged operation, boot medium or qualified profile is claimed.
[Current results and limits](release-evidence/1.9-raw-image-reader-2026-10-20.md).


## Raw metadata codec — 2026-10-20

VS-1917/#574 adds a bounded pure encoder for the frozen reader contract.
Independent golden bytes match in both geometries; invalid claims produce no
metadata. It opens no paths/devices and grants no publication, durability or
restore claim. Actual capture, staging and directory protocol remain open.
[Current codec evidence](release-evidence/1.9-raw-metadata-codec-2026-10-20.md).


## Packaged release identity — 2026-10-20

BUG-19035/#811 replaces six hardcoded stable compiler identities with an output
from validated release inputs: beta → beta, final stable → stable, unpublished
stable-version candidate → candidate. Unsupported combinations refuse. Every
platform/Store publish consumes that same value. Existing update source,
package names, source/provenance and protected release gates remain enforced.
This is required for beta-only diagnostics and truthful build information; it
neither publishes a beta nor qualifies any disk/installed update path.


## Beta-only technical diagnostics — 2026-10-20

VS-1998/#805 follows the maintainer's beta-default request. Only compiled beta
uses default-on policy 2 and expanded operation timing/cancellation/error and UI
health events. Non-beta retains explicit policy-1 opt-in. Prior refusal, private
review, stop/cancel/delete controls, policy/channel validation, bounded retention
and excluded sensitive fields remain. Separate beta choices/queues cannot enable
stable or transfer its pending reports. The schema still has exactly 18 fields.

The companion receiver accepts policy 2 only for beta; existing operator alerts
and daily summaries stay private. Public privacy text and all 19 application
locales disclose the behavior. BUG-19035 makes packaged beta identity truthful.
[Current evidence](release-evidence/1.9-beta-diagnostics-2026-10-20.md) keeps local,
native and production observations separate; release/qualification remains open.
