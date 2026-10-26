# Local validation workspace lifecycle — VS-1980 / #673

This contract supplements AGENTS.md and CONTRIBUTING.md. Existing branch,
tracking, localization, protected review, provenance and release gates remain
in force. Its purpose is to prevent completed experiments from filling the
maintainer's computer or leaving runnable test copies scattered across it.

## Before provisioning

Use one private validation root outside the repository and synchronized folders;
place each run in one dated scope directory. Keep screenshots and manuscripts in
the private editorial root. Product sources, backups and installed-app profiles
are never test scratch space. Do not put recovery experiments in a backup share.

Maintain a small private ownership inventory for each run: source SHA and dirty
state, purpose, exact created paths, process identities, input origin/hash,
expected peak bytes, regeneration instructions, retained evidence and expiry.
Do not commit host paths, raw diagnostics, credentials or machine identifiers.
Unknown files are not owned merely because their names resemble a test fixture.

Measure available host space and actual allocated bytes before large work. Count
compressed downloads, expanded images, publish copies, VM write growth, logs and
archives together. Reuse a verified existing input rather than duplicating it.
Do not start provisioning that would leave less than the larger of 20 GiB or 15%
of the host data volume free; reduce the plan or report the space requirement.
Recheck during long operations and stop safely before exhausting the budget.
Sparse virtual capacity does not bound real growth. Do not run duplicate guests
or repeatedly download removed ISOs just to restore an earlier setup.

Choose the smallest environment that can answer the current question. Native
cross-platform UI and actual recovery qualification still require their own
measured runs; unit tests or Windows PE inventory are not substitutes. No host
raw-device writes, unknown guest licenses or external storage use are authorized
by this lifecycle contract.

## Completion and interruption

1. Stop the exact owned VM, TPM, helper, server and QA app processes normally;
   verify their exit and release of files. Use recorded process identity/start
   information, not a broad executable-name kill. Leave the installed app alone.
2. Retain a compact technical receipt with source/binary hashes, invocation,
   relevant result counts, exit/shutdown outcome, limitations and regeneration
   instructions. Keep required release signing proofs unchanged. Preserve the
   minimal evidence needed for an unresolved failure before deleting inputs.
   Native screenshots stay private and captions describe only what was tested.
3. Delete regenerable installers/ISOs, disposable overlays/seeds, duplicate
   publish trees and QA bundles, obsolete synthetic images and scratch outputs
   created by this run. Never create a second full backup tree merely to clean
   the first one. Compression is useful only for a specifically retained proof,
   after round-trip verification and within the total budget.
4. Before deletion, verify canonical ownership boundaries, aliases, mounts and
   cross-run dependencies. Never recursively sweep temp globs, personal folders,
   caches, backup shares, untracked work or unknown application containers. A QA
   container may be removed only if its exact recorded synthetic ownership and
   stopped app are established. Uncertainty means preserve and report it.
5. Re-measure free/allocated space, verify no owned process remains and update
   the inventory. Report measured reclaimed bytes, not estimates from sparse
   virtual capacity. List every retained exception, its bounded size, reason and
   review/expiry date. Expiry triggers review of ownership and dependencies,
   never unconditional deletion of a path that may have been reused.

Completed runs retain compact receipts and necessary proofs only. A short-lived
active next-test dependency may remain with an explicit expiry; review within
seven days. Keep raw successful test logs for at most seven days unless a review
or failure requires a documented extension. Interrupted work must have the same
inventory, safe stopped processes and a clear remaining-size budget before handoff.
A cleanup failure is reported explicitly rather than described as completed.

## Environment availability — 2026-10-26

The maintainer removed the local validation lab, including ISO/guest/package
inputs. Inspection found no owned recovery QA/VM/TPM process and no remaining
run directory under the validation root. The host reported approximately 56 GiB
free. No space recovery from that removal is attributed to subsequent work.

Committed native receipts from earlier runs remain historical evidence tied to
their measured sources. Their private binary, guest and raw-log paths must not
be presented as currently available or reused without checking. Reconstruct
only the environment needed for a concrete next test, validate its inputs and
record a fresh result separately. Existing private editorial screenshots remain
outside GitHub. The missing environment does not change the four unqualified
recovery profiles or make the beta ready.
