# Release signing policy

VS-1902 / [#113](https://github.com/ATAC-Helicopter/VaultSync/issues/113).
Applies to every candidate, beta and stable direct-download release.

## Mandatory provenance signatures

The pinned GitHub `actions/attest` action signs provenance with GitHub/Sigstore
keyless identities. No long-lived signing key or paid certificate is required.
The canonical manifest and every asset it declares must be attested after all
packaging finishes: installers, DMGs, Linux packages, Store upload when included,
and every updater patch JSON and ZIP. SBOM signatures remain package-specific.

Before a run succeeds, `scripts/release_signing.py verify` verifies every subject
online and against a retained offline bundle and trusted roots. Verification
requires the exact repository, `.github/workflows/release-assets.yml` signer,
source commit, authorized source ref and GitHub-hosted runner. Beta sources use
`Dev`, or the sole `work/1.9-roadmap-realignment` preparation ref for
`1.9.0-beta.N` (N 1–998). Stable sources use `Stable`; reviewed candidates use
`release/<version>`. Workflow and signature inventory admission share
`scripts/release_source.py`; unrelated work branches and later-release betas
cannot use this preparation exception. The actual workflow ref and exact SHA
remain the signing identity; never label a preparation build as a Dev build.
Missing proof, unexpected assets, altered bytes or identity mismatch abort the
workflow. Draft assembly repeats verification against the original source run's
commit and `Stable` ref before uploading. Old runs without complete proof must
be rebuilt; they cannot silently bypass this rule.

The `release-supply-chain-proof` Actions artifact retains `attestations/`,
`trusted-root.jsonl`, `attestation-subjects.sha256` and `sboms/`. Archive it with
the release's source-run ID and immutable commit before publication; Actions
retention is finite. Download all attestations for archival verification using
`gh attestation download` if the workflow artifact has expired. Never replace
missing proof with an unsigned checksum list or report from another run.

For a downloaded run with assets under `release-assets/` and proof under
`supply-chain/`, supply the independently reviewed source SHA and authorized ref:

```bash
python3 scripts/release_signing.py verify \
  --asset-root release-assets --repository ATAC-Helicopter/VaultSync \
  --commit "$SOURCE_SHA" --source-ref refs/heads/Stable \
  --bundle supply-chain/attestations/release-provenance.json \
  --trusted-root supply-chain/trusted-root.jsonl
```

For a machine without network access, verify each asset with `gh attestation
verify FILE --repo REPOSITORY --signer-workflow
REPOSITORY/.github/workflows/release-assets.yml --source-ref REF --source-digest
SHA --deny-self-hosted-runners --bundle BUNDLE --custom-trusted-root ROOTS`.
Obtain trusted roots from `gh attestation trusted-root` while connected; an
archived root cannot disclose later revocations. See the official
[GitHub CLI verifier](https://cli.github.com/manual/gh_attestation_verify).

## Native publisher signing

Provenance signatures do not provide Windows Authenticode publisher identity,
Apple Developer ID or notarization. Native desktop packages are intentionally
unsigned under the current budget policy; macOS ad-hoc bundle signing remains
required for executable integrity. `VaultSyncSignatureStatus=unsigned` remains
truthful. SmartScreen/Gatekeeper guidance remains necessary. Activating native
certificates requires separately qualified packaging and updater flows before
changing this status. Store distribution retains its separate trust model.

## Operator input and evidence output

BUG-19034/#810 validates repository, full source SHA and authorized ref syntax
before invoking the verifier. The GitHub CLI executable and every subject/proof
path resolve to absolute paths; execution uses an argument vector and no shell. Long-option values use
`--name=value` so each value remains inseparable from its option.
Channel-specific source authorization and exact manifest inventory checks remain
required. Invalid arguments or a missing CLI abort before verification begins.

Checksum inventories are created exclusively as new private evidence files.
Parent traversal and ambiguous filenames are refused; existing files, symlinks
and hardlinks cannot be overwritten. Choose a fresh output filename in an owned
directory for a new run instead of replacing retained proof. Missing evidence still
requires a new qualified build. Compression benchmark JSON uses the same exclusive
creation policy; prior measurements are never silently replaced.

## Updater compatibility

Manifest schema v1, canonical names, exact asset inventory, sizes, hashes,
download URLs and final package bytes remain unchanged. Signing proofs stay in
Actions artifacts and GitHub's attestation service, outside release assets.
Existing desktop clients and patch helpers keep their current parsing and
SHA-256 checks; they do not perform Sigstore verification at runtime and do not
require an additional service for updating. Provenance is enforced during
release preparation and can be verified independently by users. SHA-256 alone
is not a substitute for verifying signer identity.

## Evidence and remaining gates

On 2026-10-13 the published 1.8.9 Windows installer passed actual GitHub/Sigstore
online verification and offline verification with workflow/ref/commit bound to
`ce0f7dbd1c674814c9454c110767986d5cc3414c`. A copy altered at byte 4096 failed
offline verification. Its original SHA-256 was
`3fcabd73673d52997743833493ccade98ae9725f235aeaa16d2fbf5e4f175b48`.
That historical release had package provenance but no manifest attestation.
Synthetic tests cover full-inventory signing boundaries, altered/missing/extra
assets, identity restrictions, links and fail-closed verifier behavior. They do
not establish new-run cryptographic signatures or platform upgrade qualification.
A successful authorized release-assets run with complete proof is still required
before publication. No release, tag or candidate was published for these checks.

## Unmerged 1.9.0 beta source — VS-1902

The maintainer requests beta distribution without merging sole release PR #736.
The build/source authorization above enables packaging from its preparation head,
not release qualification or automatic publication. Canonical version and project
metadata must first select the beta identity; the qualified-profile gate still
executes before packaging. MSStore upload is disabled for this beta request.

Existing installed updaters discover beta releases through the GitHub release's
`target_commitish=Dev` field, then compare version tags and verify the asset
manifest and bytes. Proposed staging must create and verify the beta tag at the
exact qualified preparation SHA **before** creating a release with that legacy
channel field. GitHub documents that an existing tag is not moved by
[`target_commitish`](https://docs.github.com/en/rest/releases/releases#create-a-release).
The returned API field, resolved tag, canonical manifest, source-run SHA and
actual-ref attestations must all be checked. No source or protected branch moves.
Draft assembly now includes the guarded beta path below; live staging and
installed 1.8.9 upgrade testing remain unqualified. No tag or draft was created
by implementation or synthetic tests.


### Existing beta draft assembly — 2026-10-21

After every beta gate passes and the canonical identity is cut, freeze the
qualified preparation commit. Create the beta tag at that exact SHA and stage an
unpublished GitHub prerelease with the same tag and `target_commitish=Dev`.
The assembly workflow **does not create tags, create/edit releases or publish**.
Those maintainer-controlled actions remain separate from the asset upload.
Never move a beta tag to reuse its version; a changed build needs a new beta.

Dispatch `release-assets.yml` on the same frozen source ref/version with
`prepare_draft_from_run` pointing to the successful qualified asset run. The
source run must belong to this repository, use this workflow and workflow_dispatch,
be completed/successful, and match the dispatch branch. Beta assembly requires
its checkout SHA to equal the source run SHA; a later dispatch head refuses.
The canonical manifest must match the exact repository, commit, version and
channel. Actual-ref online/offline signature checks remain mandatory before
any upload; `Dev` never substitutes for the signing source ref.

`scripts/release_draft.py` enforces the full eight direct package roles, excludes
Store uploads and requires exactly the optional patch pairs selected by dispatch
inputs. With no patches, nine files include the canonical manifest; with every
patch pair, nineteen. It rejects missing/extra/duplicate/linked/changed files.
The existing stable path still requires nineteen declared assets plus manifest,
including all patch pairs and its separately validated Store upload.

For beta, both before and after uploading, the authenticated API draft must have
`draft=true`, `prerelease=true`, the exact tag and the legacy `Dev` marker. The
remote tag must already exist and resolve to the qualified commit, with bounded
annotated-tag traversal and no branch fallback. Stable retains its exact source
SHA target and non-prerelease checks. A changed release ID refuses after upload.

The complete paginated remote asset inventory must contain only uploaded files
whose names, sizes and SHA-256 digests match local qualified bytes. Only missing
files are uploaded; differing files are never overwritten. Afterward the complete
inventory must match exactly, including the manifest. An already complete draft
is an idempotent check. Failure leaves the draft unpublished for investigation;
this job does not roll back uploads or claim a completed release.

Unit and actual-workflow-shell tests use synthetic bytes and simulated GitHub
responses, including no-patch/all-patch, partial/complete draft, tag/byte mismatch
and already-published refusal. They do not qualify native packages, new signatures,
live GitHub tag/field behavior, installed old-client discovery/upgrade or final
publication. Retained results: [draft evidence](release-evidence/1.9-beta-draft-2026-10-21.md).
