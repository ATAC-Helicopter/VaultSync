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
Draft assembly currently remains Stable-only; beta staging and installed 1.8.9
upgrade testing remain open. This increment creates no tag or draft.
