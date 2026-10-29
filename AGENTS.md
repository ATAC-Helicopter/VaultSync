# Active development: VaultSync 1.9.0

The active release is 1.9.0, unless the maintainer explicitly selects another
release. Read ROADMAP.md, release/1.9-family.json, CONTRIBUTING.md and
docs/RELEASE_1.9.0.md before implementation.

## Repository-wide agent routing

This routing policy applies to every agent touching this repository, including
single-task, directly invoked, resumed and delegated agents. Read this root file
and applicable directory instructions before work. Role files only narrow their
assigned scope; they never replace release ownership, tracking, approval,
localization, evidence or cleanup rules below. Agents without this client's
custom-role support must follow the same scope/safety rules and report that
limitation rather than claiming a model switch or silently choosing a costlier
fallback. See [routing setup and limits](docs/CODEX_ROUTING.md).

The coordinator supplies the issue/release, stable interfaces, exact disjoint
write sets, acceptance checks and owned workspace budget to each subtask.
Subagents do not create branches/issues, commit, push, publish or allocate new
IDs unless explicitly assigned; the coordinator integrates and updates tracking.
Every agent must report its touched files, real checks, limits and owned cleanup.
The configured defaults take effect when a supported client loads them; they do
not change the model of an already running coordinator turn.

Goal: minimize **total Codex Plus usage per correct, tested change**. Do not under-select for data safety. More agents are not automatically better.

**Ceiling:** never choose more than `gpt-6.1-sol` / `medium` (**GPT-6.1 Sol / medium**). No Sol high/xhigh/max or Astra. This is an instruction, **not** hard enforcement against client/user overrides. Root defaults to `gpt-6.1-terra` / `medium`; it cannot swap its own model mid-turn, but subagents can use distinct configurations.

## Pick the route from risk, uncertainty and coupling

- **Root alone, zero subagents:** small docs/fixes, routine UI, obvious isolated changes, routine test commands. Default for short work.
- **`vaultscan` (Luna Low):** independent, sizeable read-only investigation; not a single search.
- **`vaultui` (Terra Medium):** routine substantial Avalonia AXAML, styles, presentation and bindings in multi-part requests.
- **`vaultworker` (Terra High):** tricky UI async logic, substantial noncritical Core/CLI or multi-file integration/debugging.
- **`vaultcritical` (Sol Medium):** any credible risk to backup/restore, disk writes/imaging, data deletion/retention, encryption/keys, repository formats/migrations, cross-machine identity, recoverability or security; also ambiguous high-coupling architecture. Escalate **before editing**, even for tiny changes.
- **`vaultverify` (Terra Medium):** genuinely separate noncritical tests/reviews, never redundant routine checks. Critical invariants stay with `vaultcritical`.

If a cheap tier is uncertain, finds surprising coupling, cannot prove assumptions or repeatedly fails, escalate only that slice. Do not retry identical prompts. If Sol Medium is insufficient, report unverified risks rather than quietly downgrading or violating the cap.

## Multiple subtasks within one user request

Define interface, progress, cancellation, error and state contracts **before** independent edits. For worker + UI, route backend to `vaultworker` or `vaultcritical` depending on risk and presentation to `vaultui` (or `vaultworker` if complex). Run routine integration/tests in root. Spawn **at most two** specialists concurrently and only if independent work saves time/context; otherwise delegate sequentially or use root. Keep write sets disjoint, handoffs precise, and avoid duplicate scans/reviews. Finish the **entire prompt**, not just one subsystem.

## Engineering safety

Preserve existing C#/.NET and Avalonia conventions, compatibility and safe backup semantics. Paths: `src/VaultSync.Core`, `src/VaultSync.CLI`, `src/VaultSync.UI`, `tests/VaultSync.Core.Tests`. Never run destructive tests on user backups, drives or production data without explicit authorization. Make the smallest correct diff and add meaningful regressions. Run focused checks first (e.g. `dotnet test tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj`), broaden for cross-module risk, and verify UI build when relevant. Never claim unrun tests passed. Conclude with actual changes, executed checks and remaining uncertainty; name agent tiers only if used.

Detailed scenarios and measurement procedure: `docs/CODEX_ROUTING.md`.


## Approved recovery direction

The maintainer explicitly approved the joint VS-1910/1917/1918/1919 implementation
direction on 2026-10-20. The retained record is
`docs/release-evidence/1.9-joint-architecture-approval-2026-10-20.md`, hash-bound
in `release/disk-support-1.9.0.json`. Reuse that approval; do not ask again for
the same engine or joint direction. It permits implementation and disposable-VM
tests. Concrete format/protocol security checks, exact device authorization,
protected reviews and recovery/package qualification remain required. Host-device
writes and release publication are outside that approval; all profiles remain
unqualified until their own measured loop passes.
The separate `VaultSync.RecoveryHost` and `VaultSync.RecoveryController` projects
are internal recovery-media roles. Follow [the installed-entry contract](docs/RECOVERY_ENTRY_1.9.md);
never turn them into ordinary desktop elevation endpoints or treat staging/builds
as installed-boot, graphical-consent or disk-profile qualification.

## Working copy and delivery

- The assembled 1.9.0 development branch is `work/1.9-roadmap-realignment`.
  Its sole release PR is #736, targeting `Dev`. Keep this workspace on that
  branch for work that must ship with 1.9.0, and add its commits to that PR.
- `release/1.9.0` is the protected release identity; it is not the current
  assembled preparation head. Do not switch to its older tree to start work,
  rewrite it, or create another 1.9.0 release PR merely because of its name.
- Before editing, inspect Git status and the PR head. Preserve local/untracked
  work and existing commits. Follow merge-only protection and required reviews.
- Use the family manifest to check ownership. Discovery/help/completion belong
  to 1.9.1; resource CLI automation belongs to 1.9.5. Work for later releases
  goes to their own preparation branches/PRs, not into the 1.9.0 binary.
- Reuse the owning issue and update the 1.9.0 changelog, relevant contracts,
  documentation and native GitHub Project tracking with each meaningful change.
- Changelog entries use one short sentence describing one user-facing result.
  Aim for 8–14 words; 22 words is the hard ceiling, excluding the `[ID]` prefix.
  Never pad an entry to reach a word count. Move explanations into linked docs.
  Keep consecutive entries adjacent, with no blank or whitespace-only lines
  between them. Retain blank lines around release and category headings.
  Active `Fixed` entries must use the defect's sole `BUG-xxxxx` owning ID.
  Keep development/maintenance under `VS-xxxx` in the appropriate category;
  never use dual BUG/VS titles. Preserve published historical identifiers.
  See CONTRIBUTING.md for counting and detail-placement rules.
  Follow Keep a Changelog 1.1.0: `Added`, `Changed`, `Deprecated`, `Removed`,
  `Fixed`, `Security`, ISO release dates, and a linked `Unreleased` section.
- Commit and push completed, validated work to the owning release PR's
  preparation head without asking again. This is standing maintainer
  authorization. Preserve protected-branch reviews, merge-only history and
  release gates; it does not authorize bypass, automatic merge or publication.
- Never name a public branch, issue, PR, commit, project item or other artifact
  after an assistant, model, agent or AI system. Use product/release/work scope.
  Do not add automated-tool attribution to public titles or commit messages.
- With every meaningful change, update the owning issue and release PR,
  native Project #7 status and traceable dates, CHANGELOG.md, ROADMAP.md and
  relevant contracts/user docs. Use `Refs` while release-gated work stays open.
  Keep historical evidence separate from current-head test results.
- Find current preparation heads and sole PRs in the preparation map in
  docs/RELEASE_FAMILY_1.9.md. Refresh that map and owning contracts whenever
  a preparation PR is superseded; protected release identities stay separate.
- Cross-release documentation/validation standards apply to every open release
  PR, using its real head and active metadata. Never copy one release's
  unreleased features into another; `release/1.9-family.json` owns the IDs.
- Keep #736 draft until its actual release gates pass. Do not merge or promote
  a release merely because compilation or mandatory CI checks pass.

When the maintainer changes the active release, update this working context and
the release contract together so the next session starts in the correct place.

## Rider and contributor workflow

- Confirm Rider's solution projects and Git root once per session. Reuse the
  connected IDE; do not add a second MCP server for the same Rider instance.
- For text/file discovery, start with scoped `rg`/`rg --files`. For C# symbols,
  references and dependencies, use Rider's semantic tools with bounded results.
  Read only the relevant line ranges; expand when a result is incomplete.
- Use Rider's build start/state tools for incremental development, and its
  executable test locations when available. The required Release command in
  CONTRIBUTING.md remains the review gate; a Debug IDE build cannot replace it.
  Shared `VaultSync Contracts` and `VaultSync Release Checks` configurations
  provide concise repository validation; full logs and TRX paths are printed.
- Use file/batch diagnostics after code edits. An empty Problems View is not
  fresh analysis; check timeout, partial-result and unsupported-file markers.
- Use the refactoring skill and Rider for semantic refactors; use the debugging
  skill and debugger for runtime questions that source/tests cannot resolve.
  Profile only when performance evidence is needed. Database and game-engine
  tools are used only for tasks involving those systems.
- Batch independent reads and keep tool output small: consume structured
  summaries, retain full logs as local artifacts, and inspect failures narrowly.
  Do not enumerate every tool schema or repeatedly read the full roadmap.
- If an IDE tool fails or cannot discover a test, record the limitation and
  use the repository's CLI checks. Do not interpret missing IDE results as pass.
- Ask the maintainer about unresolved repository conventions or architectural
  choices after checking the canonical contracts; continue independent work.

## Local validation workspaces and cleanup

Follow [the workspace lifecycle](docs/VALIDATION_WORKSPACES.md) for every local
build, VM, package, screenshot and recovery experiment. This supplements the
existing review, signing, localization and release gates; it never replaces them.

- Use one private validation root outside the repository and sync services, with
  one bounded run directory. Record owned paths, processes, purpose, source SHA,
  expected peak size and retention before creating substantial artifacts. Keep
  screenshots/editorial material in the private editorial workspace.
- Inspect current free space and existing usable inputs before provisioning.
  Budget downloads, expanded images, copy-on-write growth and publish outputs;
  sparse virtual capacity is not the host-space budget. Do not accumulate parallel
  copies or redownload ISOs the maintainer removed without a concrete next test.
- Stop only the run's own VM, TPM, helper and QA processes when finished; verify
  termination before deleting their files. Never terminate the installed app or
  another run by a broad process-name match. Disposable guest disks are regular
  owned files, never host raw devices or existing backup destinations.
- End each completed increment by removing its regenerable ISOs, VM overlays,
  seeds, duplicate publishes/apps, obsolete synthetic images and scratch files.
  Preserve required source-bound receipts, signing proofs and unresolved failure
  evidence first. Retain only explicitly listed, bounded evidence or an active
  next-test dependency; give each exception a purpose and review/expiry date.
- Delete only proven run-owned paths after checking aliases and boundaries.
  Never sweep personal folders, projects, backups, mounted shares, global caches,
  QA containers with unknown contents or untracked repository work. Do not use
  blanket `git clean`, recursive temp globs or backup copies as a cleanup policy.
- Recheck free space, remaining processes and the ownership inventory at the end;
  report actual measured recovery and anything deliberately retained. If the
  maintainer deletes an environment, update its availability record and rebuild
  only what the next test needs. Historical receipts do not prove that deleted
  binaries, logs or VM images remain locally available.

## Native UI and hosted verification

- For isolated macOS UI checks use `scripts/build_macos_qa_app.py` and
  [the QA contract](docs/MACOS_QA_VALIDATION.md), a serviced self-contained
  development publish and a private QA container. Preserve sandbox/network
  restrictions, the narrow Launch Services entitlement and installed-app data.
  Keep synthetic diagnostic reports local. QA ad-hoc signing is not publisher,
  updater, Store, beta-channel or recovery qualification.
- Diagnose startup failures using the native crash stack and relevant sandbox
  denial before blaming session access. A valid signature/build does not prove
  launch: verify an actual visible window, required interactions, clean exit and
  relaunch. Capture real UI only; label unsupported platforms and untested flows.
- Bind hosted results to the exact PR head SHA. A successful Sonar analysis job
  is not its quality gate: inspect the separate SonarCloud result, conditions and
  matching analyzed commit. Preserve failed runs as historical; rerun appropriate
  checks after repairs without suppressing findings to make a release pass.
- Keep issue/PR bodies and native Project fields current without adding comments
  when the maintainer requests silence. Missing IDE access, deleted environments
  and unavailable native tests must be recorded, never counted as passing tests.

## Work tracking

For non-trivial code, behavior, packaging, security, release, or user-facing documentation work, keep the change tied to a GitHub issue.

Before starting new work, prefer:

```bash
./dev issue <bug|feature|change|security|maintenance> "Concise scope" --branch current
```

Use `--branch new` when the work should have a dedicated branch. The default is deliberately `current`; branch creation is a choice, not a ritual.

When a public/existing issue already describes the work, adopt it instead of creating a duplicate:

```bash
./dev adopt <issue-number> --type bug --branch current
```

The helper allocates the canonical VaultSync ID from the active release family, adds available repository labels, attempts to add the issue to Project #7, and associates the active branch with the issue in local git config.

Release-family inference order is:

1. explicit `--release X.Y`;
2. an active `release/X.Y[.Z]` branch;
3. `activeRelease.version` in `release/release-metadata.json`;
4. the linked `Unreleased` target version or legacy unreleased entry in `CHANGELOG.md`.

Use `./dev status` before committing if the branch-to-issue association is unclear.

### Before every push

For any non-trivial tracked change, work tracking is a push prerequisite:

1. Run `./dev status` and confirm the active branch is associated with the intended issue and canonical ID.
2. If tracking is missing, use `./dev issue ... --branch current` or `./dev adopt ... --branch current` before pushing. Do not reconstruct the ticket from commits afterward.
3. Confirm the issue scope/status still matches the implemented work and update required roadmap/changelog/release metadata.
   Run `./dev audit --release <owning-version>` and resolve every discrepancy
   before pushing. A missing permission, partial API result or failed audit is
   not a pass; retain the output and report the exact unresolved condition.
   Remote issue audits require complete counted cursor snapshots; ID allocation
   also covers PR titles/bodies. Missing rows, changed totals, duplicate identities,
   cyclic cursors, partial metadata or API errors fail closed before mutation.
4. A dedicated branch remains optional unless another repository/release rule requires one.
5. Tiny typo/format-only maintenance may use the repository's explicit tracking-skip mechanism; larger work must not silently bypass tracking.

### Issue and Project lifecycle

- Before implementation, reuse or create the owning issue and ensure it has
  its exact release milestone/label, one roadmap priority, assignee and native
  Project #7 item with Status, Priority, Area, Release and traceable dates.
- Starting work updates both the issue status label and Project status. Each
  meaningful increment records delivered scope, remaining acceptance, current
  evidence and its commit/PR; preserve historical results as historical.
- Keep release-gated work open and In progress after implementation. Close and
  move to Done only when its owning closure criteria pass. Conditional work
  proven unnecessary may close as not planned with linked evidence; do not
  describe that disposition as delivered functionality. Record Completed on
  from the actual closure date, never from a successful build or planned date.
- Before declaring a release tracking audit complete, run `./dev audit
  --all-releases` against the complete family manifest. Repair missing owners
  without allocating duplicate IDs or importing later-release features.


## Commit and PR references

Include the canonical ID in meaningful commit subjects when practical, for example:

```text
fix(BUG-18177): reject incomplete recovery evidence
feat(VS-1898): add recovery report export
```

PRs must contain both a canonical ID and an issue link (`Closes #N`, `Fixes #N`, `Resolves #N`, or `Refs #N`). Use `./dev pr` to create the standard PR automatically from the branch association.

Only use `Tracking: skip - <reason>` for genuinely untracked maintenance. `Tracking: bootstrap` exists only for the tracker bootstrap itself.

## Release notes

PR labels feed GitHub's native generated release-note categories through `.github/release.yml`. Preview notes with:

```bash
./dev notes --tag vX.Y.Z --previous-tag vA.B.C --target <branch>
```

The generated notes are a release drafting aid; the curated `CHANGELOG.md` remains authoritative until the release workflow explicitly changes that policy.

Release ownership and preparation instructions take precedence over this helper.
Use the existing owning issue and sole release PR, with `Refs` for release-gated work.
ID allocation fails closed if the complete remote issue audit is unavailable; it
never silently allocates using only a local roadmap. Permission or network failures
must be resolved before creating a new numbered work item.

## Release provenance signing

Candidate, beta and stable direct-download builds must attest the canonical
manifest and every declared final asset, including updater patch JSON and ZIP.
Verify all signatures online and offline against the exact source repository,
workflow, commit and authorized ref before success or draft upload. Retain and
archive the proof artifact; missing proof requires a new qualified build.
Follow [release signing policy](docs/RELEASE_SIGNING.md). Preserve updater schema
and asset names; keep native unsigned metadata truthful until platform publisher
signing is separately qualified. This shared rule applies to every release.

## Mandatory UI localization

Localize every new or changed user-visible application string in the same change,
including C# dialogs, notifications, errors, loading states, accessibility, tray
and updater outcomes. Add real translations in every maintained locale; English
fallbacks do not satisfy coverage. Preserve placeholder signatures and format
with the selected culture. Reapply cached text on language changes without losing
user state; review indirect Core messages and dynamic keys explicitly. Preserve
user data and recorded technical payloads. Run
`python3 scripts/localization_audit.py --check` and the required Release gate.
See [the localization contract](docs/LOCALIZATION.md); this is a shared rule for
every release and PR. Static checks supplement translation and rendered UI review.

## Beta diagnostic policy

Only the exact compiled beta channel enables default structured technical sending
and expanded tracing (policy 2). Stable, candidate and development retain explicit
policy-1 opt-in. Keep beta preferences/queues isolated, honor prior refusal, fail
closed on invalid/unwritable choice storage and never transfer default beta
permission or pending reports into stable. Disclose and localize the behavior;
keep review/disable/delete controls. Preserve fixed allowlisted fields and bounds;
never upload raw logs/dumps, user content, paths, credentials or persistent user/
device/session identifiers. Follow docs/PRODUCT_DIAGNOSTICS.md on both client and
receiver. The maintainer explicitly selected this beta-only default on 2026-10-20.

## Private editorial material

Development diary manuscripts, Reddit drafts and publication packages belong in
a private editorial workspace outside this public repository. Do not commit or
upload them to GitHub, issues, pull requests or attachments. Keep technical
release evidence in its owning documentation directory; link published articles
on FG Labs when useful. Approved publication of a new first-party website devlog
also authorizes its automatic notification to confirmed followers of that product,
respecting unsubscribe state and durable duplicate protection. Follow the owning
website publication contract; previews and unrelated sends require their own
maintainer authorization.

## Risk-based, targeted verification (mandatory)
- Do **not** automatically run the full test suite, a solution-wide build, repository-wide lint/typecheck, or repeated test cycles for every edit. Minimize unnecessary tool calls, time, and token use.
- Before testing, assess the changed files, affected behavior, and realistic regression risk. For docs, text/copy, comments, formatting, and other clearly non-behavioral changes, **skip automated tests** unless a specific gate applies; inspect the diff instead.
- For behavior-changing code, use **only the smallest relevant existing test(s)**, focused test filter, package, assembly, or project. Run at a meaningful checkpoint, not after each trivial edit. Avoid redundant dependency installs/restores.
- If no relevant automated test exists, use a focused static/manual check when feasible; say what remains unverified. Do not run unrelated tests merely to claim coverage.
- Expand to integration tests, broader builds, or the full suite **only when justified** by shared/core changes, significant refactors, security/privacy changes, migrations, release gates, failure of focused checks, or explicit user instruction. Explain the reason and avoid unnecessary repeats.
- Respect genuinely mandatory CI/release/security gates. Interpret generic existing instructions such as "run tests before commit" as **scoped, risk-based validation**, not permission to test the entire repository on every small change.
- In the final report, state what was tested, what was deliberately skipped, and any remaining risk. Never claim skipped tests passed.
