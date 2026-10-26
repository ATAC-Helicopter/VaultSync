# VaultSync 1.9.0 — Disk and Bootable Recovery Foundation

Planning release; current stable is 1.8.9. Stable target: 2027-03-26.
Active development branch: `work/1.9-roadmap-realignment`; sole release PR:
[#736](https://github.com/ATAC-Helicopter/VaultSync/pull/736) into `Dev`.
Protected release identity: `release/1.9.0`; its older ref is not the assembled
working tree. Promotion to `Stable` uses reviewed merge commits.
No disk support or predecessor upgrade is qualified yet.

BUG-19040/#819 corrects the private macOS QA package's missing Launch Services
permission. Native startup, onboarding and Recovery rendering now pass in
separate synthetic profiles, with actual screenshots retained privately. The
[QA contract](MACOS_QA_VALIDATION.md) and create-only builder preserve sandbox,
container isolation and network denial; this is not a release signing or restore
qualification. Earlier blank captures/launch failures are not proof that the UI
control connection caused this crash.

VS-1907/#495 adds bounded operation framing and authenticated read-only image
descriptor handoff. An independent unprivileged producer/root consumer passes
153 checks in the latest disposable offline VM run. This is transport evidence, not a writable
host, actual human/device authorization, media or a qualified restore profile.
See the [operation transport contract](RECOVERY_OPERATION_PROTOCOL_1.9.md).
The S3869 follow-up uses private native descriptor copies instead of borrowing
SafeHandle numbers; a fresh 153-check native run retains source/PDB binding and
separately records original-handle closure and copy cleanup. Hosted verification
remains a distinct gate.

VS-1907/#495 now connects Recovery image selection and cancellable read-only
inspection to the isolated Linux x64 reader, with all 19 locales and explicit
macOS/Windows limitations. Destination authorization, writing and media are not
connected to this panel yet. See [the UI contract](DISK_RECOVERY_UI_1.9.md);
this increment does not satisfy the complete beta or support-profile gates.

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
[current readiness](release-evidence/1.9-beta-readiness-2026-10-20.md) and
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


## Unmerged beta build source — 2026-10-20

VS-1902/#113 permits beta asset builds from the sole preparation ref only for
1.9.0-beta.N, using one shared workflow/signature admission policy. Dev remains
compatible; other work refs and later-release identities refuse. Actual source
SHA/ref binding, canonical/project versions, all signatures and the qualified
disk-profile gate remain intact. No version cut, tag, package, draft or beta is
created by this change. Beta draft staging and installed 1.8.9 qualification
remain open; [signing policy](RELEASE_SIGNING.md#unmerged-190-beta-source--vs-1902)
records the required tag/channel separation.
[Current policy evidence](release-evidence/1.9-beta-source-2026-10-20.md).
#736 remains draft and unmerged.


## Read-only raw capture and private publication — 2026-10-20

VS-1904/#492 connects the held Linux x64 source to the frozen raw metadata/reader
pipeline. Kernel-read-only source, fresh native descriptor checks, fixed private
ext4 storage, allocation preflight, bounded streaming, source GPT recheck and
independent output readback precede flushed rename-no-replace publication.
Cancellation before commit stays partial; failed post-rename sync is explicitly
unconfirmed. No image or empty directory can be replaced. See
[the storage/capture contract](LINUX_RAW_CAPTURE_1.9.md). This opens no writable
device and supplies no authenticated privilege, desktop/media integration,
image-to-device restore, power-failure qualification or supported profile.

[Current capture evidence](release-evidence/1.9-linux-raw-capture-2026-10-20.md):
1,693 Core / 242 Python pass; 20 native virtual-device/storage cases pass in a
network-free UEFI VM. Small GPT fixtures do not qualify a filesystem or boot
profile. Earlier lab failures and native-versus-synthetic cases remain separate.


## Protected raw staging — 2026-10-20

VS-1907/#495 snapshots bounded input metadata, verifies original bytes/GPT before
allocation, copies into private preallocated storage, and verifies the copied
bytes against that original snapshot. Sealing persists 0400 permissions and
reopens matching inodes with kernel read-only descriptors before closing every
writer. A further readback and flushed create-only publication precede an owned
staging session. Changed source/copy/inode, invalid seal, cancellation or I/O
failure grants no restore-input capability. This is same-privilege Core work;
no device write, target authorization, authenticated worker, desktop/media or
qualified recovery profile is supplied. See [staging contract](RAW_IMAGE_STAGING_1.9.md).

[Current staging evidence](release-evidence/1.9-raw-staging-2026-10-20.md):
1,710 Core / 242 Python pass; 31 native capture/staging cases pass, including
kernel read-only descriptor checks and original-file mutation isolation.
These small fixtures do not qualify boot or image-to-device recovery.


## Native restore target observation — 2026-10-20

VS-1905/#493 now resolves original/staging filesystem ancestry from held
descriptors and a fresh kernel inventory, excluding repository disks before
opening a target. Exact capacity/logical/physical geometry, unchanged selection
and the existing native read adapter precede a held read-only observation.
Rechecks expire after 30 seconds and invalidate on stage/identity changes.
Unknown use is never promoted to Offline or permission; no device write is
implemented. [Native target contract](LINUX_RESTORE_TARGET_PREVIEW_1.9.md).
Authenticated worker, exact consent/UI, media and complete recovery qualification
remain open; four profiles remain candidates and #736 remains draft.

[Current native observation evidence](release-evidence/1.9-native-target-preview-2026-10-20.md):
1,737 Core / 242 Python and 43 native capture/staging/target checks pass.
Targets retain their independently checked bytes; no writable device is opened.
These are prerequisites, not boot/restore or supported-profile qualification.


## Current beta readiness — 2026-10-20

VS-1980/#673 reconciles the [current readiness snapshot](release-evidence/1.9-beta-readiness-2026-10-20.md)
with code head 99773d2, its 1,737 Core / 242 Python and 43 native cases.
Approved direction and delivered native prerequisites are distinct from the
remaining authenticated worker, writable restore, independent media, translated
user workflow, complete recovery and installed update/signing gates. Four profiles
remain candidates. No beta scope reduction, version, publication date, asset run,
tag, publication or merge is selected by this update. #736 remains draft.


## Explicit target binding checks — 2026-10-20

VS-1905/#493 replaces two implicit zero-byte ReadAsync probes with an explicit
serialized descriptor validation API. This addresses hosted S2674 findings on
99773d2 while retaining the native identity checks, no-byte-read behavior, source
closure after change and pre-inspection cancellation. No Sonar rule, threshold
or exclusion is changed. The old head's failed reliability gate remains
historical; a new exact-head analysis is required after delivery.

[Current explicit-check evidence](release-evidence/1.9-explicit-binding-check-2026-10-20.md):
1,740 Core / 242 Python and 43 native read-only cases pass. Hosted reanalysis
remains separate; the five-stage recovery and beta gates remain open.


## Native overwrite review exchange — 2026-10-21

VS-1905/#493 adds operation-bound, single-use overwrite review descriptions over
verified staged images and read-only native target claims. Each acknowledgement
requires a fresh independent inventory, repository exclusion and held descriptor
check. Image root/generation, target and full overwrite range remain fixed;
review replacement, wrong operation/review, replay and expiry refuse. UTC and
monotonic deadlines prevent clock rollback from extending the claim. No user
interaction, Offline authority, privilege or device-write capability is inferred.
The translated dialog, authenticated broker and writable restore remain open.
See [contract](LINUX_RESTORE_TARGET_PREVIEW_1.9.md) and
[source-bound evidence](release-evidence/1.9-overwrite-review-2026-10-21.md).

Hosted SonarCloud and CodeQL completed successfully on the previous exact head
`b30e355ce4a999ef23f5df21b15dfb902e047d5a`; the earlier failed 99773d2 result
remains historical. This increment requires its own hosted checks after push.
All four disk profiles remain candidates; PR #736 stays draft and unmerged.


## Guarded beta draft assembly — 2026-10-21

VS-1902/#113 extends the existing unpublished draft assembler to the exact
qualified beta preparation commit. Actual source-ref provenance is reverified
online/offline, the pre-existing tag must resolve to that commit, and the existing
private prerelease must retain `target_commitish=Dev` for old-client discovery.
Eight direct package roles are mandatory; Store is excluded and requested patch
pairs must be exact. Complete paginated remote names/sizes/SHA-256 are checked;
only missing assets upload and changed files refuse. The tag/draft identity is
rechecked afterward. Stable keeps its full nineteen-asset-plus-manifest matrix.

This implements guarded assembly, not live release qualification: canonical beta
cut, a real successful package/signature run, exact 1.8.9 discovery/upgrade,
recovery/profile qualification and protected reviews remain open. The workflow
never creates/moves a tag, creates/edits/publishes a release or merges #736.
See [signing/staging contract](RELEASE_SIGNING.md#existing-beta-draft-assembly--2026-10-21)
and [current evidence](release-evidence/1.9-beta-draft-2026-10-21.md).


## Draft input security follow-up — 2026-10-21

BUG-19034/#810 addresses hosted S8707 on d798e58: CLI-selected JSON paths are
removed in favor of fixed workspace files and before/after phase choices.
Bounded UTF-8 reads reject escape/traversal, links, replaced descriptors and
invalid JSON. Existing draft identity, source/ref/commit signatures, complete
matrix and no-overwrite checks remain mandatory. No rule or threshold is lowered.
See [current boundary evidence](release-evidence/1.9-draft-input-boundary-2026-10-21.md).
This is a tool correction, not beta/recovery qualification; #736 stays draft.


## Kernel worker peer binding — 2026-10-21

VS-1904/#492 adds an internal Linux x64 Unix-stream peer binding prerequisite.
Expected connect-time PID/UID/GID must match kernel credentials; a socket-bound
pidfd pins the exact process lifetime without a numeric-PID fallback. Mismatch,
unsupported kernel options, disconnect/exit and failed checks refuse; owned
close-on-exec process descriptors are released without closing caller sockets.
It consumes no application bytes and grants no privilege or operation authority.

This is not integrated into the existing inventory pipes, trusted build selection,
per-message/operation authorization or a privileged restore broker. Those remain
open with unprivileged image isolation, offline authority, media/UI, device writes
and complete recovery/upgrade/package qualification. All profiles stay candidates.
See [worker contract](DISK_WORKER_1.9.md#kernel-peer-binding-prerequisite--2026-10-21)
and [source-bound evidence](release-evidence/1.9-worker-peer-2026-10-21.md).

The preceding exact head `37f6999f683a21327c5d7b701a466df65cb4b865` passes hosted
Sonar and the remaining checks, including the fixed draft-input S8707 boundary.
The failed d798e58 analysis remains historical. This new source needs its own
hosted checks; no quality gate is weakened and #736 stays draft/unmerged.


## Peer native lifetime protection — 2026-10-21

BUG-19034/#810 addresses hosted S3869 on 6a69f75 by replacing manual handle
extraction with SafeHandle-based epoll interop. The pidfd and monitor remain
close-on-exec and owned; acquisition/refusal/disposal release both. Peer-death
and inherited-socket checks retain their fail-closed behavior. No rule, threshold
or exclusion is changed. See [current interop evidence](release-evidence/1.9-peer-safe-interop-2026-10-21.md).
This remains an internal peer prerequisite, with no privileged protocol or raw
write integration; four disk profiles stay candidates and #736 stays draft.


## Actual inventory peer integration — 2026-10-21

VS-1904/#492 now uses kernel peer binding in the actual Linux x64 inventory
parent and child. Private startup precedes any request; the child stays alive
until the bounded correlated response is validated and acknowledged. Matching
child/kernel-parent PID/UID/GID, socket-bound lifetimes, EOF, empty stderr and
exit zero are mandatory. Unsupported binding refuses without pipe-only fallback.
Windows/macOS/other architectures retain their existing development transport.

Current required Release: 1,797 Core / 261 Python, zero warnings/errors/skips;
1,943 keys / 19 locales. Actual isolated UEFI VM: 92 cases, including 24 new
integration checks, eight real worker runs, nobody-UID completion, malformed/
interrupted peers, post-ACK trailing/nonzero refusal and cleanup/no descriptor
leaks. Normal guest/QEMU exit zero, no timeout; 77.56 host-monotonic seconds is total boot/probes.
No host raw-device writes/network/shared folder. See the
[contract](DISK_WORKER_1.9.md#inventory-peer-integration--2026-10-21) and
[source-bound evidence](release-evidence/1.9-worker-channel-2026-10-21.md).

This advances inventory integration only: trusted installation/publisher checks,
per-message origin, image sandbox, privilege/offline/device-operation authority,
native writable recovery/journal/read-back, media/UI and full recovery/installed
upgrade/package/signature/review gates remain open. Four disk profiles stay
candidates. #492/native Project stay In progress; #736 stays draft/unmerged.
Exact delivered-head hosted checks remain required. No beta cut/tag/draft,
release asset run or publication occurred.


## Internal device writes and full byte read-back — 2026-10-21

VS-1907/#495 adds an internal engine consuming an exclusively owned sealed stage,
already-held target and durable-journal provider. Image/operation/review/target
bindings are rechecked before writes. Each bounded range has a prior durable
intent, target flush and subsequent durable acknowledgement; failure retains
acknowledged/uncertain ranges without rollback/resume claims. The complete target
payload and GPT must pass the independent raw verifier before WrittenAndReadBack.
The Linux adapter opens no path; it adopts only a matching held O_RDWR block
handle without append/direct mode and with close-on-exec. No public device writer,
elevated endpoint, desktop route or production journal acquisition is added.

Required Release: 1,834 Core / 261 Python, zero warnings/errors/skips;
1,943 keys / 19 locales. The actual pinned isolated UEFI VM passes 32 new native
checks, including capture/stage, exact new disposable targets, review correlation,
read-only/append refusal, cancellation after durable intent with unchanged bytes,
real writes/flushes, journal order and full read-back for 512/4096 sectors.
After normal poweroff the host independently confirms both new raw target files
match their source SHA-256. QEMU/guest exit zero, no timeout; 49.00 seconds is whole
boot/probes, not throughput. Tiny GPT fixtures do not contain a bootable OS.

The earlier 92 inventory/peer/capture cases remain historical at 72f9e6d; they are
not relabeled as rerun by this new 32-case probe. No host raw devices/network/share
were attached. See the [write-engine contract](RAW_DEVICE_RESTORE_1.9.md) and
[source-bound evidence](release-evidence/1.9-device-restore-2026-10-21.md).
Trusted broker/role/build and exact per-operation device authority, controlled
offline/repository/journal ancestry, unprivileged image isolation, real recovery
media/UI, power-cut and full filesystem/boot/interactive qualification, installed
upgrade and package/signature/protected review gates remain open. Four support
profiles stay candidates; #495/native Project stay In progress, #736 draft.
This is an internal byte-write prerequisite, not a feature-complete beta.


## Restore cleanup flow follow-up — 2026-10-21

BUG-19034/#810 addresses hosted S2583 on fcb47e8's cleanup flag assigned inside
finally. Existing tests execute both successful and failed cleanup; no native
corruption was measured. An explicit release-result helper now preserves those
outcomes and target release after unexpected provider exceptions, without
suppressing the rule or weakening a gate. Feature owner remains VS-1907/#495.

Current required Release: 1,835 Core / 261 Python, zero warnings/errors/skips;
1,943 keys / 19 locales. The fresh isolated UEFI run repeats all 32 native
write/read-back cases on two new disposable target files, with host-independent
source SHA-256 equality, normal QEMU/guest exit zero and no timeout. Its 53.48
seconds is total boot/probes, not throughput. No host raw device/network/share.
The fcb47e8 scan and earlier native results remain historical. See
[current source-bound evidence](release-evidence/1.9-restore-cleanup-2026-10-21.md).
Trusted writer/authority/journal integration, image isolation, independent
media/UI, full recovery/OS boot, installed upgrade/package/signature/review gates
remain open. Four support profiles remain candidates; #736 stays draft/unmerged.
New exact-head hosted verification is required; no release publication occurs.


## Private persistent restore journal — 2026-10-22

VS-1907/#495 replaces the lab-only JSONL provider with an internal version-1
binary journal. It independently checks target/boot/namespace and the held
parent's unique filesystem ancestry before any entry is created; journal storage
on the target, unknown or changed ancestry refuses. Preallocated private ext4
files are flushed/published without replacement. Bounded hash-linked records
validate exact operation/image/review/range ordering. An unconfirmed append
latches closed; inspection never promotes a truncated history into verification
or grants resume/device authority. The engine handles journal binding/terminal
refusals as controlled failure, including cleanup after an existing terminal.

Current required Release: 1,910 Core / 261 Python, zero warnings/errors/skips;
1,943 keys / 19 locales. Seventy-three journal cases and two engine integration
regressions are new. A permanent Python-generated independent byte fixture checks
header/framing/hash order. Final isolated UEFI VM: 43 native cases, including the
actual product journal for both geometries, interruption/read-back reopen,
private modes, parent refusal before any entry, duplicate/stale refusal, corruption
and truncation. Full target hashes independently match both original sources on
the host after normal poweroff. QEMU/guest exit zero, no timeout; 52.05 seconds is
boot/probes, not throughput. No host raw devices/network/shared folder.

A prior hash-probe advisory sharing failure and an overly narrow negative-test
exception expectation remain separate failed lab attempts; no product admission
is weakened. See [journal contract](RAW_RESTORE_JOURNAL_1.9.md) and
[source-bound evidence](release-evidence/1.9-restore-journal-2026-10-22.md).
Trusted writable broker/role/build/consent, image sandbox, controlled offline
state, journal lifecycle/retention, independent media/UI, physical power-cut and
full filesystem/OS-boot/interactive recovery, installed upgrade/package/signature
and protected review gates remain open. Four support profiles stay candidates;
#495/native Project stay In progress and #736 stays draft/unmerged. This is not
a feature-complete beta or a release asset/publication run. New exact-head hosted
verification remains required.

## Exclusive native restore acquisition — 2026-10-22

VS-1904/#492 adds the internal Linux x64 acquisition prerequisite for the existing
VS-1907 writer. Strict request/image/geometry binding, live unstarted bound journal,
unique original/staging/journal ancestry, fresh boot/namespace/target observations
and held native identity precede ownership transfer. openat2 rejects links in
all components, requests O_RDWR/O_EXCL and refuses unsupported kernels without
fallback. Competing claims, file/link substitution, cancellation and changed
identity/topology close without writing. No worker/UI endpoint or authorization
capability is added; exact consent, trusted role/build and offline authority stay
required. See [contract](LINUX_RAW_RESTORE_OPEN_1.9.md).

Current required Release: 1,955 Core / 261 Python, zero warnings/errors/skips;
1,943 keys / 19 locales. The isolated pinned UEFI VM passes 59 actual native cases,
including both 512/4096-sector writes/read-back and new opener refusals. Independent
host hashes match both restored targets to original fixtures after normal poweroff.
This is tiny GPT byte-image evidence, not filesystem/OS-boot recovery qualification.
See [source-bound evidence](release-evidence/1.9-restore-open-2026-10-22.md).
The earlier journal and worker results remain historical at their exact sources.
Trusted writable worker/protocol, image sandbox, independent media/UI, full recovery,
installed upgrade, signed package and protected-review gates remain open; all four
profiles stay candidates. #736 remains draft/unmerged, without beta cut or release.

The same-day hosted follow-up separates acquisition ownership from typed admission,
uses explicit terminated UTF-8 bytes at the syscall boundary, checks the descriptor
conversion and keeps native declarations in their owning adapter. It addresses
new maintainability findings without changing admission, native flags, journal
order or supported profiles; final source is rebuilt and rerun in the VM.
The first 2ae7c24 hosted results remain historical, not final-source evidence.

## Isolated image reader — 2026-10-22

VS-1904/#492 adds an actual separate Linux x64 image reader. After trusted module
warmup, it drops root privileges, validates zero capabilities and synchronizes a
compiled syscall allowlist across all threads. The parent supplies only bounded
byte ranges of two held read-only streams: no paths, device handles or writable
operation. Fixed binary results bind image facts and an independent whole-payload
SHA-256; parent kernel-state checks, terminal ACK, EOF, empty stderr and exit zero
remain mandatory. See [reader contract](IMAGE_INSPECTION_WORKER_1.9.md).

Current source required Release: 2,036 Core / 261 Python, zero warnings/errors/skips;
1,943 keys / 19 locales. Final isolated VM run passes 30 native cases, including
actual 512/4096 image verification/corruption/truncation, root-to-nobody and already-
nonroot launch, forbidden file/network/exec operations on old/new threads, independent
kernel status, blocked-read cancellation and deadline cleanup. Earlier bootstrap,
thread-retirement and runtime-abort attempts are retained separately. Native trace
identified CLR self-affinity initialization; the filter permits only PID-zero
self-affinity while keeping filesystem/network/foreign-process prohibitions.
See [source-bound evidence](release-evidence/1.9-image-inspection-2026-10-22.md).

This is the constrained reader component, not mandatory staging/writer integration.
Current staging and restore still parse in their caller; the production broker
must move that parsing to the reader and bind its exact sealed bytes/hash. Trusted
build/role/protocol review, device/offline consent, independent media/UI, complete
recovery and installed-upgrade/package/signature/protected-review gates remain open.
No support profile is qualified. #492/Project remain In progress; #736 stays draft,
without beta cut, tag, release assets/publication or merge.


## Sonar coverage collection follow-up — 2026-10-22

BUG-19025/#779 follows the actual b0a5e68 hosted result: Windows/Linux/macOS
build/test, release profiles, scripts, tracking and CodeQL pass. Sonar's workflow
finishes but its quality gate fails at 33.0% coverage: the Ubuntu 26.04 rollout
failed to initialize the collector and imported no C# coverage. The preceding
Ubuntu 24.04 analysis measured coverage successfully. Pin that runner and require
covered Core source ranges before import; no rule, threshold or exclusion changes.

VS-1904's inspector follow-up separates host dispatch and parent request context,
clarifies names/statement boundaries and asynchronously cancels pending I/O during
cleanup. Exact wire bytes, sandbox policy and required native scenarios remain.
See [source-bound follow-up evidence](release-evidence/1.9-sonar-coverage-2026-10-22.md)
for current Release, native results and the independent hosted gate. The earlier
inspection evidence remains historical at its exact source. Reader-first staging/
restore integration, four-profile recovery qualification and the remaining beta
gates stay open; #736 remains draft, with no release publication or merge.


## Mandatory reader-first staging and byte-only restore checks — 2026-10-22

VS-1907/#495 now requires the isolated reader for raw staging preflight and the
sealed private copy. Every bounded fact and whole-payload hash must match; missing,
failed or timed-out inspection never falls back to the caller's parser. The
session binds exact metadata/payload hashes and lengths, carries bounded geometry
and no declared-origin/tool/coverage graph. Parent-side fixed bounded hashing
checks the sealed bytes before publication and before Started. The write engine
verifies full target hash equality without parsing hostile metadata/GPT.

BUG-19036/#812 was discovered by the native controlled-owner-mutation probe:
128 KiB read buffers could retain old sealed bytes across seeks, permitting a
write before final read-back detected the changed payload. Read-only native
reopens now disable managed buffering; transfer callers retain bounded buffers.
The fix preserves same-inode/seal checks, journal/native write ordering, exact
hashes, target exclusivity and the administrator/same-UID trust limit.

Current required Release passes **2,061 Core / 270 Python**, zero warnings/errors/
skips; localization **1,943 keys / 19 locales**. The final pinned native VM passes
**36 integration cases**, guest/QEMU exit zero, normal shutdown, no timeout.
Targets/journals in this run are byte-array/in-memory fixtures, not raw devices or
durable-journal qualification. Audits pass **46 current-release / 107 family owners**
with zero discrepancies.

See [current source-bound measurements](release-evidence/1.9-reader-staging-2026-10-22.md)
for Release and native results, failed probes and exact-head hosted verification.
Earlier inspector/write/journal numbers remain historical at their original
sources. This integration does not qualify a trusted installed writable broker,
exact-device/offline/consent authority, operation lifetime, recovery media/UI,
full filesystem/OS-boot/crash/power-cut loops, installed upgrades or signed assets.
All four profiles remain candidates and #736 stays draft/unmerged. No beta
cut/tag, release asset publication or protected review bypass is introduced.


## Complete remote tracking snapshots — 2026-10-22

BUG-19037/#813 was discovered at the mandatory push audit for the reader-staging
increment: REST enumerations returned varying incomplete subsets and omitted
known owners still available by direct lookup. Independent counted GraphQL reads
returned all 569 issues and 170 PRs before this tracker ticket was created.
The exact remote service/CLI cause is not established; no discrepancy was ignored.

Audit enumeration now follows issue cursors to stable totalCount with unique
identities and complete labels/assignees. ID reservation also reads all PR titles
and bodies. Missing rows, changed counts, duplicates, bad/cyclic cursors, partial
metadata and API errors fail closed before mutation. Fifteen new regression
methods retain this shared standard; native Project fields are checked separately.
Current required Release passes **2,061 Core / 285 Python**, zero warnings/errors/
skips, localization **1,943 keys / 19 locales**. Core sources are unchanged from
the preceding source-hash-bound **36-case** native staging run; this tooling change
is not another native recovery measurement. See
[current tracking evidence](release-evidence/1.9-tracking-cursors-2026-10-22.md).

Protected reviews, native authorization/media/UI/recovery loops, installed upgrade
and signed packages remain release gates. #736 stays draft/unmerged; no beta
cut/tag/assets/publication. Missing or partial remote data never justifies a gate
bypass or locally inferred ID allocation.


## Capture inspection integration — 2026-10-23

VS-1904/#492 now routes initial/post-copy GPT inspection through the restricted
reader, using a separate bounded flat protocol. The parent encodes observed
layout fields but never parses GPT/image structures. Initial failure precedes
output allocation; after copying, native files are sealed at 0400 and reopened
read-only with writable transaction handles closed. Mandatory isolated image
inspection and independent exact metadata/payload hashing precede publication.
No in-process fallback exists for missing/failed/timed-out readers.

The original image protocol, peer/kernel-policy checks and write authorization
gates are preserved. October 23 required Release passes **2,109 Core / 285 Python**,
zero warnings/errors/skips, and the final source-bound VM passes **74 cases**,
including actual capture from two read-only synthetic GPT guest block devices.
These are not filesystem/OS-boot profile qualification; the October 25 delivery
reconciliation preserves the original measurement date. See [capture contract](LINUX_RAW_CAPTURE_1.9.md) and
[current source-bound results](release-evidence/1.9-capture-inspection-2026-10-23.md).
Installed trusted worker/build/role, controlled offline operation/device consent,
bounded writable lifetime, media/UI, complete filesystem/OS-boot/crash/power-cut
recovery, installed upgrade, signed packages and protected reviews remain open.
All four profiles remain candidates; #492 stays In progress and #736 draft.
No beta tag/assets/publication or merge is introduced by these checks.


## Packaged read-only worker integrity — 2026-10-25

VS-1904/#492 adds a separately staged self-contained Linux x64 reader closure,
strict bounded installation manifest and held-file checks in public inventory,
image and GPT clients. Exact source/role, identical parent/helper Core bytes,
SHA-256, ownership, modes and authenticated child executable are checked before
requests; changed files refuse response acceptance. Cleared runtime environments,
existing kernel peers, protocol bounds and cancellation remain in force.
No unqualified adjacent development worker becomes a production fallback.

The helper receives the same validated release channel and source properties as
the application. Existing package/patch names and updater schema remain intact;
final provenance, installed upgrade and portable deployment remain unqualified.
This is local integrity under a trusted launching UID/root, not publisher trust,
elevation or writable authority. Offline source/exact-device consent, bounded
writable operations, independent media/UI and full filesystem/OS-boot/crash/power
recovery still block a complete-feature beta. All four profiles remain candidates;
#492 remains In progress and #736 draft. See the
[worker contract](DISK_WORKER_1.9.md#local-installation-integrity-and-packaging--2026-10-25)
and [current results](release-evidence/1.9-worker-installation-2026-10-25.md).

The follow-up aligns worker/desktop Core target negotiation after a real workflow-
property publish exposed RID-neutral versus RID-specific bytes. A fresh actual
pair now matches; Linux CI stages the helper before AppImage packaging to enforce
this integration before the qualification-gated release job. This build check
is separate from the retained 84-case native measurement and installed qualification.


## Worker staging path confinement — 2026-10-25

BUG-19038/#815 addresses Sonar's path-traversal finding in the newly added build
script. Both source and destination must resolve inside repository/system/runner
build temporary roots. Unsafe aliases and escapes refuse before helper execution
or filesystem mutation; filesystem root cannot become an allowed boundary.
This is build tooling confinement, not native device authority or package/recovery
qualification. Required checks and the exact-head security gate remain separate;
#815 stays In progress and #736 draft. See the
[worker contract](DISK_WORKER_1.9.md#build-path-confinement--bug-19038--815).

[Current path-confinement results](release-evidence/1.9-worker-staging-roots-2026-10-25.md) retain the failed preceding security gate separately from fresh local/VM checks.


## Owned restoration and full OS backend loop — 2026-10-26

VS-1907/#495 connects native review, sealed staging ownership, fresh inventory,
durable journal, exclusive target acquisition, actual writes/read-back and cleanup
in one consumed operation. Duplicates cannot cancel an active owner; interruption
and close failures preserve uncertainty and cannot report overall success.
BUG-19039/#816 replaces the reader's tiny digest exchanges with exact-length
128 KiB reads, including warmup, while retaining existing protocol and deadlines.

Current source-bound checks pass **2,170 Core / 293 Python**, zero warnings/errors/
skips, **1,943 keys / 19 locales**. A 3 GiB UEFI GPT/ext4 source is captured,
retained through an independent recovery restart, restored with the source absent,
verified byte for byte and booted with matching fixture hashes. Four native
partial-write/cancellation targets cover 512/4096 sectors with durable honest
outcomes. There are **64 named native assertions** plus separate boot/hash checks.
[Current evidence and failed attempts](release-evidence/1.9-owned-restore-2026-10-26.md).

The recovery guest uses injected lab executables, not generated product media.
Production worker/device/human authorization, reachable translated UI, product
media, full matrix/power-loss qualification, installed upgrade, packages/signatures
and protected reviews remain open. All four profiles stay candidates and #736
stays draft. This measurement does not authorize a beta cut or publication.

The S8949 hosted follow-up explicitly uses uncancellable ownership/cleanup waits
so cancellation cannot strand resources. Fresh required Release remains 2,170
Core / 293 Python, zero warnings/errors/skips; original native source/PDB results
remain historical rather than relabeled as a new run. No rule or gate is waived.
