# Recovery evidence, semantic limits, and qualification scope (1.9)

**Status:** Maintainer design clarification, 2026-11-11. This document defines
how 1.9 work should describe and qualify evidence. It is **not** a shipped
feature, serialized schema migration, new release date, universal recovery
guarantee, or permission to relax an existing safety/release gate.

**Authority:** [ROADMAP](../ROADMAP.md) defines immutable owners and release
scope; [recovery identities](RECOVERY_IDENTITIES_1.9.md) defines the current
approved direction for observations and dependencies; the
[staged beta program](BETA_PROGRAM_1.9.0.md) owns 1.9.0 beta admission.
This contract elaborates those documents. If they conflict, stop and review;
never infer support from the existence of this document.

## Why this distinction matters

A stored backup, a byte-verified image, a completed write/read-back, a bootable
restored OS, a working application, and an independently survivable copy are
**six different assertions**. They depend on different evidence and failure
domains. A passing assertion cannot silently certify any other assertion.

**Semantic uncertainty is intrinsic.** The meaning, safety and usefulness of
data depend on the reader, version, platform, runtime state and external
dependencies. A byte-identical database may contain an application-inconsistent
state; a system may boot but fail to unlock credentials, start a service, open a
document in a newer app version, renew a license, or access an external API.
No finite generic backup checker can establish that *all* arbitrary restored
applications and data will be correct. Neither undecidability arguments nor
build reproducibility remove the need for finite, scoped operational tests.

### Independent claims, not a universal score

| Claim | Minimum acceptable observation | Does **not** establish |
| --- | --- | --- |
| **Recorded** | A bound recovery-point descriptor was written and can be discovered | Availability, content integrity or restore success |
| **Payload verified** | Declared bytes were actually read and checked against a separately bound expected digest/length and applicable format checks | Correct application state, target restoration or bootability |
| **Restored / read-back verified** | An isolated operation wrote a specific authorized target, then independently re-read and compared the defined byte scope | Boot, application consistency, hardware portability |
| **Restored OS boot observed** | A documented isolated cold boot of the restored *target only* reaches named objective OS milestones | Other OS profiles, user applications or later boots |
| **Application/service probe passed** | A named versioned checker executed a documented observable operation, with explicit expected result and bounded scope | Other apps, future versions, global consistency, business correctness |
| **Failure independence assessed** | A declared dependency/failure-domain scenario has measured or simulated evidence, identified as such | Survival of unknown correlated failures; physical/site independence merely from separate paths |
| **Recovery drill current** | Evidence is still applicable to the identified content, environment, dependencies and policy at evaluation time | A perpetual guarantee or stronger evidence than the underlying drill |

Claims are **orthogonal**: an app-specific check is not a substitute for raw
read-back, and a byte check is not a substitute for application checks. Avoid
a single `fully recoverable` badge or a numeric score that conceals unknown
claims. Where legacy 1.8.x wording exists, preserve truthful backward-compatible
behavior while adding explicit scope instead of silently changing its meaning.

### Result and applicability are separate

The proposed presentation/protocol concepts below are **design targets**, not
new already-implemented wire fields:

- **Execution result:** passed / failed / interrupted / not run / inconclusive.
- **Applicability:** supported / unsupported / not applicable / unknown.
- **Freshness:** current / stale / unknown, with recorded evaluation time,
  expiry or policy decision.
- **Provenance and coverage:** subject/generation/content root, independently
  bound expected values, producer/tool and **exact build**, validation method,
  trusted measurement source, environment/profile, covered bytes/objects,
  observed UTC time, result reason, artifact pointer and redactions.
- **Claims:** separate typed facts for payload integrity, restored bytes,
  independent boot, declared application probes, and failure-domain analysis.

A simulated boot plan is not a boot measurement; a user-confirmed login is not
an automated full-application check. Missing or unsupported evidence does not
become a failure of stored data, and must never become a pass. Record a failed
attempt without overwriting previous successful observations. If a reader,
format, identity or signer is unknown, fail closed for destructive decisions.

Changing the content root, source generation, key/credential, relevant tool
or target profile, or policy applicability may stale a result. Renaming a
location does not change immutable content but **does** require a fresh access
observation. A last successful check from months ago must retain its timestamp.

## 1.9.0 installed evidence and its limits

On 2026-11-10, an **installed, disposable 3 GiB Debian GPT/UEFI/ext4 VM**
completed capture, a separately verified recovery copy, graphical restore,
full destination read-back, whole-image comparison, and independent target-only
cold boot. The original source and repository were detached for the final
boot; fresh firmware variables and no network restricted hidden dependencies.
The measured record is the
[installed Linux OS loop receipt](release-evidence/1.9-installed-linux-os-loop-2026-11-10.json).

This is evidence **for that exact fixture and build**, not public beta admission,
a packaged rescue-media qualification, multi-terabyte testing, hardware
certification or blanket Linux/Windows/macOS support. In particular:

- Partial black redraw in the recovery controller is still under repair.
- Final physical media, capacity envelopes, interruption/refusal and
  application/configuration continuity require separate evidence.
- Shared coverage, packages, upgrades, signatures/provenance, updater and
  protected-review gates remain mandatory; do **not** lower the 80% new-code
  coverage threshold to compensate.
- A checksum success is not an independent restored-OS boot.
- A successful boot is not a complete application/configuration qualification.

The public stable version remains **1.8.9**. The 1.9.0-beta.1/2/3 names in the
[beta program](BETA_PROGRAM_1.9.0.md) describe intended staged **disk**
qualification, not released assets. The desktop application remains supported
on Windows, macOS and Linux across those stages.

## Verification options and safety boundaries

| Mode | Appropriate scope | Costs and limits | Scheduled ownership |
| --- | --- | --- | --- |
| Read-only metadata/manifest check | Discovery and corruption preflight | Metadata may be well formed while payload is absent/broken | Existing recovery logic / 1.9.0 |
| Full payload check | Actual stored bytes, declared digest/length | I/O time, inaccessible encrypted or remote content; wrong shared oracle can hide an implementation bug | Existing engine / 1.9.0 |
| Isolated restore + independent read-back | Real destination write correctness, cleanup and refusals | Needs disposable capacity, strict target binding and negative tests; does not prove boot | 1.9.0 qualification |
| Isolated VM cold-boot drill | Measured OS boot for a **qualified, compatible** image | Not applicable to every image, can execute untrusted code, cannot prove real-hardware/driver continuity | 1.9.0 laboratory gate; optional scheduling investigated in 1.9.5 |
| Manual/physical recovery drill | Measured hardware, peripherals and actual user workflows | Time, spare disks, physical risk and human judgement; must record operator and exact scope | 1.9.0 stable profiles / later assurance |
| Application/service probes | A named database/application version and check contract | No universal probe; may mutate recovered state, need consent and isolated targets | Optional extensible assurance, not mandatory for every 1.9.0 profile |
| Scheduled reminders/drills | Evidence freshness and operator-controlled assurance | Compute, capacity, media, trust and missed-run limits | 1.9.5; **not** automatic VM boot for every backup in beta.1 |

**Default safety posture:** do not execute arbitrary restored executables,
boot user-supplied images, connect recovered guests to networks, mount
potentially hostile filesystems writable, or load user-provided checker scripts
automatically. A VM is a containment measure, **not** a guarantee of safety.
Require explicit opt-in, trusted/qualified runners, disposable targets, least
privilege, bounded CPU/RAM/space/time, network-off by default, and clean
shutdown/cleanup. Any production capability to run application probes needs
a separately reviewed threat model, source trust and a user-visible consent
contract. Test fixtures may be executed in controlled labs under different
documented policies; lab-only harnesses do not create an end-user feature.

**For application consistency**, a safe image capture path should declare
whether the source was offline, quiesced or crash-consistent; do not infer
database transaction consistency from an image hash. WAL/journals, open file
handles, service dependencies, keychains/DPAPI, TPM/Secure Boot, UEFI NVRAM,
licensing, machine identity and external data dependencies may affect restored
behavior. Unsupported consistency semantics must be reported explicitly.

## Qualification and regression matrix

For every **advertised disk profile** record, at minimum:

1. OS/version/build, architecture, filesystem(s), disk/partition geometry,
   sector size, firmware/boot mode, boot loader, encryption, Secure Boot,
   capture state, storage/recovery media, worker and tool build, whether it ran
   in a VM or on physical hardware, and profile identifier.
2. Capture source and separated destination observations, permission/authority
   checks and measured payload length/digest with independently chosen oracle
   where possible. A verifier using the same flawed reader is not independent.
3. Negative cases: short/truncated reads, stale buffer data after seeks,
   mismatched digests, missing/changed target, source/target confusion, stale
   physical confirmation, cancellation, lost power/process, unmounted/changed
   storage and cleanup. Each result includes what was refused *before* writes.
4. Verified output/copy or staging lifecycle; target write+full independent
   read-back; exact bytes covered and any exclusions.
5. Cold boot **without source/repository attached**, with fresh firmware
   variables when applicable; explicit boot marker/service state, console and
   graphical interaction where advertised. No success from a previously
   booted source or mounted recovery repository.
6. Separately declared app/service/file checks, if offered; name checks **not
   run** rather than claiming general application continuity.
7. Scope of VM vs physical hardware, case count, failures, unsupported cases,
   dates, independent review and open blockers.

The support matrix is a **list of positive, measured profiles plus refusals**,
not a guess that similarly named systems will work. A disposable VM passing
does not qualify the equivalent physical machine. Expand profile coverage only
after owned tests. Beta.1 Linux, beta.2 Windows and beta.3 macOS have independent
implementation/security requirements. Never project a Linux x64 recovery
environment onto Apple Silicon/APFS without its own measured path.

A negative test that shows a broken fixture is correctly *rejected* may be
better safety evidence than a positive checksum test. Keep failure cases in
CI/qualification, and retain regression coverage for read-back and true boot
as separate gates. Stale, inconclusive, unsupported and not-run observations
must not be silently counted toward release admission.

## Public UX and wording

Use precise, scoped labels such as:

- **Stored copy found** (not yet checked).
- **Payload verified** (named content and last check timestamp).
- **Restore and read-back passed** (target and bytes covered).
- **Restored OS boot observed** (exact OS/profile, VM or physical, and date).
- **Application check passed** (named app/version and explicit test).
- **Not tested / unsupported / stale / incomplete / refused** as distinct
  non-success states.

Show a claim's method, environment, time, provenance, coverage and known
limitations in details. Aggregate readiness may summarize **available evidence**,
never imply semantic validity or universal recovery. Historical 1.8.x proof
must remain explicitly file-level. Planned presentation work belongs to
**1.9.4**; scheduled freshness/VM drill controls belong to **1.9.5**.
A release note must not advertise these as existing 1.9.0 functions.

Avoid public claims such as `100% recoverable`, `all data guaranteed`, `tested
on every Linux system` or `bootable because checksum passed`.

## Reproducible builds and version-dependent semantics

Record exact capture/recovery/restore **binary build identities**, source
revision and toolchain/platform observations in qualification evidence where
privacy permits. Reproducible builds and supply-chain signatures help establish
software provenance; they do **not** prove restored data or application behavior.
Hardware-targeted optimization (for example `-march=native`) may reduce binary
portability even when the source code is the same. A mismatch is a recorded
compatibility limitation and a separate [release-signing](RELEASE_SIGNING.md)
concern, not evidence that an application is consistent or inconsistent.

## Release ownership: existing IDs only

| Release | Clarification, not a new unconditional feature gate |
| --- | --- |
| **1.9.0** | VS-1918/1919/1908/1909: explicit profiles, separate byte/restore/boot evidence, no general application guarantee; preserve existing beta and stable gates |
| **1.9.1** | VS-1911–1914: read-only browse/extract/check evidence remains **inspection**, not recovered OS/app proof |
| **1.9.2** | VS-1931–1934/1976: independently recover on clean machines, explicit key/reader dependencies and version limits |
| **1.9.3** | VS-1923–1925/1927: remote manifest vs retrieved bytes vs clean-machine restore, each recorded independently |
| **1.9.4** | VS-1942/1944: transparent, accessible multi-claim display; no all-green single verdict |
| **1.9.5** | VS-1951–1954/1956/1979: opt-in bounded drills, freshness/alerts and typed structured reports; VM/app probes require reviewed safety and consent |
| **1.9.6** | VS-1983/1987/1990: BSC object integrity, materialized changeset, and optional app/build checks stay distinct |
| **1.9.7** | VS-1988/1991/1993: replication/merge/large-history evidence stays scoped to explicit bytes and supported backends |
| **1.9.8** | VS-1961–1963: persist/version this evidence distinction, migration and LTS compatibility, long-duration qualification |

The exact canonical ID mapping, including older imported aliases, is in
[family alignment](RELEASE_FAMILY_1.9.md). Do not renumber GitHub issues,
alter version dates, change the JSON format or wire protocol, or shift owners
merely to adopt these semantics. Any future executable probe or persisted
schema must be designed, reviewed, tested and scheduled independently.

## Implementation review checklist

- [ ] Every claim has a named check, bounded input/coverage and truthful result.
- [ ] Separate passes for content, restore/read-back, boot, apps and failures.
- [ ] Negative tests show incorrect data or authority is **refused**, not blessed.
- [ ] True independent VM and physical hardware observations are distinguishable.
- [ ] User consent and sandbox boundaries exist before arbitrary boot/probes.
- [ ] CLI/API/UI export never upgrades not-run/unsupported/stale to success.
- [ ] Reader/writer/build provenance and test environment are explicit.
- [ ] Historical 1.8.x proofs and 1.9 binary formats retain compatibility.
- [ ] Applicable beta/QA/coverage/security/package/upgrade gates all pass before
      an advertised support profile or release is promoted.
