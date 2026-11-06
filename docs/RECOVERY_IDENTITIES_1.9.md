# Recovery identities and evidence — VS-1919 review draft

Current direction: **approved by the maintainer on 2026-10-20**;
[retained joint review](release-evidence/1.9-joint-architecture-approval-2026-10-20.md).
Implementation/security finalization and all support/release qualification remain open.
Earlier proposed statuses below are historical.


Proposed contract for [VS-1919 / #576](https://github.com/ATAC-Helicopter/VaultSync/issues/576),
prepared 2026-10-04 for 1.9.0 / PR #736. This defines semantics for review, not
an approved schema, integrated readiness evaluator or supported restore path.
Review alongside [VS-1917](DISK_IMAGE_FORMAT_1.9.md), VS-1918 engine/support
and [VS-1910 routes](ROUTE_ARCHITECTURE_1.9.md). Existing backup metadata
keeps its current format and migration rules.

## Identity and generation

Assign an opaque random identity to each logical entity; preserve it when that
same entity is copied or renamed. A new capture, credential enrollment, recovery
medium build or replacement physical device creates its own identity. Never
identify an entity solely by its path, display name, mount letter, hostname or
partition GUID: these can change or collide. Unknown identity remains unknown.

| Entity | Identity binding and observations |
| --- | --- |
| Device | Stable local registration plus observed hardware identity, capacity and sector geometry; identical/cloned identifiers require explicit ambiguity handling |
| Site | User-defined physical or administrative boundary; location and ownership are observations, not automatic failure independence |
| Repository | Logical recovery collection, format and generation; moving it changes its locator rather than authorizing a different collection |
| Destination | Registered storage endpoint and generation, linked to repository, device/site/provider and access dependencies |
| Credential | Non-secret enrollment reference and generation; credential material stays in its existing secret store |
| Key | Non-secret key acquisition reference, encryption profile and generation; possession, usability and recovery availability are separate observations |
| Recovery medium | Immutable build/content identity, tool/driver profile and recorded boot qualification; a successful build does not prove bootability |
| Tool | Product, engine, adapter and build/source identity; versions alone do not establish binary authenticity |
| Recovery point | Immutable capture/content-root identity and declared scope, linked to source, payload parts and required ancestors |
| Operation | Unique attempt identity and ordered state events; retries/resumes retain explicit links rather than overwriting earlier failures |
| Evidence | Immutable observation identity, subject generation, producer, scope and result; later observations supersede rather than edit earlier facts |

A logical identity is not destination authorization. Re-observe the selected
physical destination immediately before an authorized destructive operation.
Reject a changed, missing or ambiguous device and invalidate the old preview.

## Dependency graph

Each edge declares its relation and applicability: needs payload, parent image,
key, credential, compatible reader, boot driver, network, service or access
permission. Bind immutable payload dependencies to content identities. Store
locator hints separately; an untrusted image cannot create authorized network
access, secret lookup or device operations just by supplying a path or URL.

Represent alternatives explicitly. Two mirrors may satisfy one payload need,
while payload and key are both required. Reject unsupported dependency types,
cycles and excessive graph depth before evaluation. Missing parent or key
produces a named unsatisfied requirement, never an implicit success.

Evaluation is read-only and bounded. A failed check does not mount a device,
fetch credentials, launch tools, mutate repositories or restore bytes. Any
required action enters its own permission and operation contract.

## Failure domains

Track physical device, controller, host, power/network, site, provider/account,
repository administration and credential/key custody as separate dimensions.
Allow shared and unknown domains; labels such as “offsite” do not establish
independence. Two directories on one disk share its physical failure domain.
Two cloud buckets in one account share account/access risks even if their
storage locations differ. An emergency key kept only beside its encrypted
image is not independent key recovery.

A scenario removes specified entities or domains and evaluates the remaining
explicit dependency alternatives. Report unknown correlation and unsupported
scenario checks. Candidate Resilience/BSC may consume this graph later without
adding a new 1.9.0 feature or blocking Recovery Horizon on those future products.

## Evidence provenance and scope

Each record carries subject identity and generation/content root, observation
kind, producer/tool build, observed UTC time, optional expiry, environment and
method, scope/coverage, outcome and reason, and links to retained artifacts.
Record actual execution and interrupted/failed outcomes. A report checksum
binds the report bytes; it does not prove the producer was trustworthy.

| Kind | Permitted interpretation |
| --- | --- |
| Recorded fact | Metadata declares a property; this alone does not verify stored bytes or physical conditions |
| Measured evidence | A specified check ran against bound subjects and records coverage and result |
| Simulation | A model evaluated an explicit scenario; no live recovery was performed |
| Inference | A conclusion follows named observations and assumptions, with uncertainty visible |
| User confirmation | A person confirmed a precise question and scope at a time; this is not an automated measurement |
| Missing | No applicable evidence exists; do not translate absence into pass |
| Stale | Evidence no longer applies because time, generation or dependencies changed |
| Unsupported | The declared checker cannot assess this scope; do not translate it into failure of the underlying bytes |

Keep execution result (passed, failed, interrupted), freshness and support as
separate fields. A previously passed measurement may be stale. An unsupported
check may still have a recorded user confirmation. Avoid a single Boolean that
loses these distinctions.

## Readiness and invalidation

An evaluator emits per-requirement evidence and unresolved dependencies, with
an explicit policy/version and evaluation time. It must not claim universal
recoverability. A verified image proves only the named byte coverage; booting
media proves only that boot environment; an isolated restore drill proves its
recorded target and validation scope. None substitutes for the complete
Capture → Validate → Boot → Restore → Validate release qualification loop.

Invalidate applicability when bound content/generation changes, a dependency
is replaced/revoked, a required tool becomes incompatible, or a policy expires
the evidence. A renamed path alone does not invalidate immutable content, but
availability at the new locator needs a current observation. Offline/unreachable
is distinct from missing/corrupt. Preserve historical records when invalidated.

After restoring, compare recorded logical bytes and check the declared boot
and application scope. A process existing without a desktop window is not a
successful desktop-startup check. Record the actual visible/interactive result
or state that it was not measured.

## Privacy and portability

Recovery bundles export only the metadata needed to locate and validate their
declared scope. No plaintext secret, credential token, recovery key or raw
command argument belongs in evidence, public logs or diagnostics. Redact host,
serial, account and path observations from public exports. Redaction records
which fields were omitted without forging the identity being checked.

Portable readers inspect identities/dependencies without the original SQLite
database. Secret acquisition stays explicit and local. Missing enrollment or
unsupported provenance fails with a named limitation; portable tools never
silently import a foreign secret reference into the local trusted store.

## Review and qualification work still required

- Approve entity generations, observation precedence, privacy/export policy and
  compatibility with existing backup identities and metadata migration.
- Align content roots and checkpoints with VS-1917, and producer trust,
  supported checks, privileges and engine isolation with VS-1918.
- Define serialized versions and bounded graph/event limits before code depends
  on stable wire contracts; unknown required fields must reject without writes.
- Establish fixtures for identity collisions, cloned GUIDs, cycles, missing keys,
  revoked access, changed payloads, interrupted checks, clock/expiry boundaries,
  unknown failure correlation, privacy redaction and portable inspection.
- Qualify independent boot/restore and distinguish measured, simulated and
  confirmed results in the eventual UI/CLI. No such implementation is claimed.

## Observation applicability prototype — 2026-10-09

`RecoveryObservationService.Assess` evaluates one caller-supplied in-memory
observation against an explicit subject UUID, generation, exact scope, evaluation
time and positive maximum age. It retains the original observation while
classifying applicability as Missing, Current, Stale or Invalid with a named reason.
Absent identities, producer build, generation/scope and unknown enum values are
invalid provenance. Future observations and expiry before observation are invalid
chronology. Subject, generation or scope changes invalidate applicability;
expired observations remain historical results. Age is inclusive at its maximum;
an explicit expiry is exclusive. Equivalent offset timestamps compare as UTC instants.

Basis, execution outcome and checker support remain independent from freshness.
Only a current, supported, passed measurement sets `HasCurrentPassedMeasurement`.
Simulations, inferences and confirmations never become measurements, and current
failed/interrupted checks retain those outcomes. This property describes only
the supplied observation, not overall recovery readiness or producer authenticity.
The fixture producer strings are synthetic and do not prove executed recovery.

The prototype has no wire encoding, storage/migration, observation selection or
conflict precedence, dependency/failure-domain graph, trust validation, artifact
inspection, privacy export or UI/CLI integration. These remain joint review work.
Existing backup metadata is unchanged. Separately, the existing
`RecoveryConfidenceService` now rejects future-dated passed verification/drill
observations as stale, using existing warning codes and refresh actions.

## Dependency applicability prototype — 2026-10-12

`RecoveryDependencyService` adds a read-only in-memory graph to the observation
prototype. Each node binds one subject, generation, scope and supplied observation.
Its requirements are conjunctive: payload and key both need coverage. Each named
requirement explicitly lists alternatives: either measured payload copy can cover
that requirement. Empty alternatives and absent subjects remain unsatisfied;
the requirement retains its name, relation and candidate identities. Known nodes
retain their separate observation freshness, outcome, support and basis, including
failed alternatives when another candidate supplies coverage.

The entire supplied graph is validated before evaluation, including disconnected
nodes and unselected alternatives. Unsupported relations, duplicate subject IDs,
duplicate requirement names, duplicate/empty candidate identities, missing node
bindings, cycles and excessive input return an explicit invalid result with no
assessment. Limits are 256 nodes, 1,024 requirements, 1,024 candidate references
and 64 nodes on any dependency path. Shared DAG paths obey the longest-path limit
independently of input order. Missing references are requirements to resolve,
not an implicit passed leaf. A missing root has an explicit missing-root result.

`HasCurrentMeasuredCoverage` means only that the supplied root and every required
group have current, supported, passed measurements under the supplied age policy.
It does not authenticate producers, inspect retained artifacts, acquire keys,
locate payloads or prove boot/restore. Even that limited result requires the
root's own applicable measurement. Result collections own their copied candidate
lists; evaluation never edits observations or caller lists.

The graph is a review prototype with synthetic fixtures, no wire schema,
persistence/migration, conflicting-observation selection, failure-domain scenarios,
device/provider access or UI/CLI integration. Unknown failure correlation and
joint architecture approval remain open. See [validation and limits](release-evidence/1.9-dependencies-2026-10-12.md).

## Failure-domain simulation prototype — 2026-10-12

`RecoveryFailureScenarioService` models the loss of one explicitly supplied
failure domain against the dependency prototype's immutable assessed baseline.
Domain identities are opaque GUIDs separated by physical device, controller,
host, power, network, site, provider/account, repository administration and
credential/key custody. It never derives independence from paths, names or
an offsite label. Bindings refer to exact subject identities and generations;
missing generation, unknown subjects, unsupported kinds, duplicate bindings
and changed generations reject the input. At most 1,024 bindings are accepted.
Multiple known domain memberships are allowed; loss of any one affects that subject.

The entire graph and every binding are validated before a modeled conclusion.
Only graph subjects reachable from the requested root participate. A missing
binding for the selected dimension, or an explicit null domain identity, means
unknown correlation. A known matching domain removes that subject in the model.
Known and unknown memberships may coexist; an unknown additional membership
remains uncertain even when another domain is explicitly supplied.

The result distinguishes invalid input, insufficient baseline measurements,
modeled coverage loss, unknown correlation and modeled retained coverage.
Incomplete baseline evidence is not reported as a loss caused by the scenario.
Known removal is evaluated first. If coverage survives, a conservative second
evaluation also excludes unknown memberships. Retained coverage requires a
complete path through explicitly known surviving alternatives; an unknown
unused alternative remains visible without invalidating that path. A key lost
with the original image cannot be replaced by an independent payload mirror.

Every scenario result has `Basis = Simulated`, including retained coverage.
The original observation records and measured outcomes remain unchanged in the
baseline. The operation never reads a device, discovers domain membership,
acquires a secret, writes metadata or performs restore. Supplied bindings are
model assumptions, not authenticated observations or proof of independence.
Serialized formats, membership provenance/freshness, joint architecture approval,
multi-domain scenarios, portable inspection and UI/CLI integration remain open.
See [scenario validation](release-evidence/1.9-failure-scenarios-2026-10-12.md).

## Combined scenarios and ambiguous observations — 2026-10-12

`AssessMultiple` models the union of up to 32 explicitly failed domains. Duplicate,
empty and unsupported domain sets reject; the original single-domain API delegates
to the same evaluator. Two copies can survive either loss separately and still
lose coverage when both domains fail. Membership must be known in every selected
dimension; physical-device and provider-account identifiers are never conflated.
All results remain Simulated, with unchanged measured baseline records and copied
failure lists. Model membership provenance/freshness and live qualification stay open.

`RecoveryObservationSelectionService` evaluates up to 256 supplied candidates.
Invalid chronology/provenance, duplicate evidence identities and null candidates
reject without falling back to an older success. Stale or differently bound records
remain inspectable but cannot be selected. Exactly one current record is selected,
preserving its outcome, support and basis. No current record means missing applicable
evidence. Multiple current records are Ambiguous even if both passed or one is newer;
there is no approved producer/chronology precedence to silently choose between them.
Simulations or confirmations never become measurements merely through selection.

These remain review prototypes without observation discovery, automatic conflict
resolution, producer authentication, persistence or UI/CLI integration. The shared
architecture gate is open. See [batch evidence](release-evidence/1.9-contract-batch-2026-10-12.md).

## Regular-file image origin binding — 2026-10-15

VS-1917's provisional manifest schema 2 binds source registration/observation IDs,
writer-observed UTC milliseconds, geometry, GPT layout and caller tool/build
claims to the same completion root as raw bytes. `DeclaredOrigin` is explicitly
untrusted attribution data: UUIDs do not prove device identity, and checksums do
not establish producer authenticity. Full file reconstruction compares actual
GPT observations before success; generic payload verification alone does not.
No external locator, physical serial or secret is exported. The explicit scope
is regular-file fixtures, never privileged device permission or complete recovery
evidence. See [the format boundary](DISK_IMAGE_FORMAT_1.9.md#provisional-sourcelayouttool-binding--2026-10-15).

## Combined implementation review — 2026-10-20

The [joint decision package](ARCHITECTURE_REVIEW_1.9.0.md#implementation-decision-package--2026-10-20)
provides the concrete first-profile recommendation and remaining implementation
checks alongside the other three contracts. It remains proposed, not approved
or qualified. No persisted format, privileged operation or support status changes
merely because this review candidate exists.

## Joint direction approved — 2026-10-20

The maintainer explicitly approved the combined package published in 484be54;
[the retained review](release-evidence/1.9-joint-architecture-approval-2026-10-20.md)
records actual scope, reviewer and observation time. This selects implementation
direction while concrete serialization/API/security work and qualification
remain open. Historical proposed/draft status above describes earlier increments.
No persisted data or supported-device status changes merely from this approval.
