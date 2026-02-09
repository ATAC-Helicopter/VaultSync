# Roadmap

## Priority legend
- `P0` Critical: core reliability/security and release blockers.
- `P1` High: major UX/product impact for the target release.
- `P2` Medium: valuable but can slip without harming release quality.

## Planning convention
- Use `VS-xxxx` IDs as the default planning unit for roadmap items, implementation tasks, and release work.
- ID pattern:
  - `VS` = VaultSync work item.
  - First two digits map to release family (`15xx` for `1.5`, `16xx` for `1.6`, etc.).
  - Last two digits are sequence numbers inside that release stream.
- Use these IDs consistently in:
  - `ROADMAP.md` task tracking.
  - PR titles/descriptions and commit messages (when applicable).
  - Test/QA references.
- `CHANGELOG.md` rule:
  - Use `VS-xxxx` IDs for actual feature entries.
  - Cleanup/doc-only/test-only notes may omit IDs unless they map to a planned roadmap item.
- If a task spans releases, split it into release-specific IDs to keep scope and acceptance criteria explicit.

## Completed (highlights)
- [x] Updater stabilization: relaunch fixes, clearer status, patch compatibility guardrails.
- [x] Documentation refresh: expanded wiki for setup, usage, and troubleshooting.
- [x] `1.3.0` macOS support: bundled per-arch rsync + updater/patch flow validation.
- [x] `1.3.0` Cross-machine migration and metadata sync (import/merge/tombstones/safety checks).
- [x] `1.4.0` Per-project destination selection (All/Auto/Specific).
- [x] `1.4.0` Faster snapshot scanning (scan cache + aggressive mode + cadence safeguards).
- [x] `1.4.0` Dry-run backup estimates (size/time/capacity warnings + throughput-based ETA).

## 1.5.x (current focus)

### 1.5.0 priorities
- [ ] `P0` Backup encryption and password-protected backups.
- [ ] `P1` Backup bandwidth limits and quiet hours.
- [ ] `P1` Incremental backup UX improvements.
- [ ] `P2` Snapshot diff summaries.

### 1.5.0 scope and contracts

#### `P0` Backup encryption and password-protected backups
- Core scope:
  - Per-project + global encryption settings.
  - Password-protected encrypted backups (no plaintext secrets stored).
  - AES-256 + per-backup salt/IV; KDF via PBKDF2/Argon2 profile.
  - Encrypted backup container under vault path (for example: `.vaultsync/vault/`).
- Integration contract:
  - Discovery/readability:
    - Backup discovery must work for both encrypted and plain backups.
    - Encrypted entries still expose non-secret metadata (time, size, destination, source machine, keep/protected state).
  - Metadata sync:
    - Export/import must work for mixed encrypted/plain history.
    - Sync includes only non-secret crypto descriptor fields (encrypted flag, algorithm/KDF profile, format/version, parameter identifiers).
    - No passwords, raw keys, or recoverable secrets in metadata payloads.
    - Tombstones/merge behavior remains unchanged.
  - Existing feature compatibility:
    - Delete/open/keep/retention/destination scan/import keep working unchanged for encrypted and plain entries.
    - Backup-all and per-project routing behavior does not change.
    - Imported-history pause checks treat encrypted entries as normal history entries.
  - Restore/verify:
    - Password prompt only when restoring encrypted backup.
    - Wrong password fails safely (no partial writes).
    - Integrity checks run after decrypt stage and preserve current verification semantics.
    - Plain backups restore exactly as today (no prompt).
  - Key handling:
    - Password material only in OS secure store (or session memory when explicitly allowed).
    - Config and metadata store only non-secret references/parameters.
    - If secure store unavailable, fallback must be explicit and user-confirmed.
- Delivery phases:
  1. Format + schema contract.
  2. Encrypted write path.
  3. Password-gated read/restore path.
  4. Mixed encrypted/plain interop and migration behavior.
  5. UX hardening (errors/recovery messaging).
- Acceptance:
  - Encrypted backup is unreadable without password.
  - Valid password restore succeeds; invalid password restore fails safely.
  - Existing plain backups remain fully functional.
  - Metadata sync carries encryption metadata but no secrets.
  - Mixed `1.4`/`1.5` environments do not corrupt sync state.

#### `P1` Bandwidth limits and quiet hours
- Scope:
  - Bandwidth caps for network copy/archive workers.
  - Quiet-hours scheduling for defer/pause/start policy.
  - Current policy visible in UI and logs.
- Delivery phases:
  1. Config model + settings UI.
  2. Throttling enforcement in transfer paths.
  3. Quiet-hours runtime behavior.
  4. Status visibility in active cards/tray/logs.
- Acceptance:
  - Effective transfer caps are respected.
  - Quiet-hours policy is predictable and visible.

#### `P1` Incremental backup UX improvements
- Scope:
  - Clarify full/incremental/imported terminology.
  - Surface retention outcome per backup.
  - Add restore guidance by selected backup type.
- Delivery phases:
  1. Terminology cleanup.
  2. History metadata chips/outcome line.
  3. Restore helper content.
  4. Docs/wiki parity.
- Acceptance:
  - Backup type and retention outcome are clear at a glance.
  - Restore behavior is understandable before start.

#### `P2` Snapshot diff summaries
- Scope:
  - Top changed folders/files.
  - Added/modified/deleted and net size delta.
  - Exportable summary (text/JSON).
- Delivery phases:
  1. Compute/store summary stats.
  2. Surface in Projects/Backups.
  3. Export action.
- Acceptance:
  - Accurate summaries with minimal UI delay.

### 1.5.0 UI map
- Encryption UX:
  - Settings: global encryption section, policy toggles, key profile, warning copy.
  - Projects: inherit/override mode per project.
  - History/cards: encrypted badge and source-machine badge (when available).
  - Restore: password dialog for encrypted entries, explicit wrong-password and corruption flows.
  - Notifications: dedicated messages for decrypt success/failure states.
- Bandwidth + quiet-hours UX:
  - Settings scheduler card with timezone and caps.
  - Active backup cards show `Throttled` / `Quiet hours` policy chips.
  - Tray shows current policy state.
- Incremental UX:
  - History chips for type (`Full`, `Incremental`, `Imported`).
  - Retention outcome line in details.
  - Restore confirmation "what happens next" block.
- Snapshot diff UX:
  - Compact summary panel in Projects/Backups.
  - Export summary action in details.
- UI quality gates:
  - No clipping in common windowed sizes.
  - All new text localized.
  - Keyboard-accessible actions/dialogs.
  - Color status always paired with text.

### 1.5 implementation order
1. Encryption format/schema contract.
2. Encrypted write/read path.
3. Bandwidth + quiet-hours policy.
4. Incremental UX clarity pass.
5. Snapshot diff summaries.
6. Stabilization pass + release gate.

### 1.5 ticket backlog (execution-ready)

#### `P0` Encryption and password-protected backups
- [x] `VS-1501` Crypto format + metadata contract (schema/versioning).
  - Scope: define encrypted container descriptor, algorithm/KDF parameter identifiers, and migration-safe format versioning.
  - Depends on: none.
  - Acceptance tests:
    - Unit: descriptor serialize/deserialize round-trip with version field preserved.
    - Unit: metadata export payload includes only non-secret crypto fields.
    - Integration: existing plain backup metadata still parses unchanged.
- [ ] `VS-1502` Encrypted write pipeline.
  - Scope: produce encrypted backup artifacts (AES-256 + per-backup salt/IV) in vault storage path.
  - Depends on: `VS-1501`.
  - Acceptance tests:
    - Integration: encrypted backup artifact differs from plaintext source and cannot be opened as plain archive.
    - Integration: backup job reports success and emits encrypted flag in metadata.
    - Regression: plain (unencrypted) backup flow remains unchanged.
- [ ] `VS-1503` Password-gated restore/decrypt pipeline.
  - Scope: restore path prompts for password only on encrypted entries and fails safely on wrong password.
  - Depends on: `VS-1501`, `VS-1502`.
  - Acceptance tests:
    - Integration: valid password restores complete and verification passes.
    - Integration: invalid password returns explicit error and leaves no partial restored files.
    - Regression: restoring plain backups requires no password and matches current behavior.
- [x] `VS-1504` Secret handling and secure-store fallback policy.
  - Scope: store password material only in OS secure store or session memory when explicitly user-approved fallback is selected.
  - Depends on: `VS-1501`.
  - Acceptance tests:
    - Unit: config persistence never contains plaintext password/key material.
    - Integration: secure-store unavailable path requires explicit user confirmation before continuing.
    - Security check: diagnostic export redacts all secret-like fields.
- [ ] `VS-1505` Mixed encrypted/plain interop + metadata sync compatibility.
  - Scope: preserve merge/tombstone/import behavior across mixed `1.4` and `1.5` machines.
  - Depends on: `VS-1501`, `VS-1502`, `VS-1503`.
  - Acceptance tests:
    - Integration: import/export round-trip with mixed encrypted/plain history works without data loss.
    - Integration: `1.4` client ignores unknown crypto descriptors without corrupting sync state.
    - Regression: delete/keep/retention/destination scan/import behavior remains stable.

#### `P1` Bandwidth limits and quiet hours
- [ ] `VS-1510` Config model + settings UI for caps and schedule.
  - Scope: settings schema, validation, timezone-aware quiet-hours range editor.
  - Depends on: none.
  - Acceptance tests:
    - Unit: invalid caps/schedules are rejected with actionable validation messages.
    - UI: settings persist and reload accurately across restart.
- [ ] `VS-1511` Transfer throttling enforcement.
  - Scope: apply effective bandwidth cap to archive upload/network copy workers.
  - Depends on: `VS-1510`.
  - Acceptance tests:
    - Integration: measured throughput stays within configured cap tolerance.
    - Regression: no cap configured preserves current throughput behavior.
- [ ] `VS-1512` Quiet-hours runtime policy engine.
  - Scope: defer/pause/start rules based on local time and running backup state.
  - Depends on: `VS-1510`.
  - Acceptance tests:
    - Integration: backup start during quiet hours follows configured policy deterministically.
    - Integration: crossing quiet-hours boundary transitions active jobs predictably.
- [ ] `VS-1513` Policy visibility in cards, tray, and logs.
  - Scope: expose effective policy state (`Throttled`, `Quiet hours`) in UI and operational logs.
  - Depends on: `VS-1511`, `VS-1512`.
  - Acceptance tests:
    - UI: active cards and tray always show current policy state when applicable.
    - Log check: policy transition logs are informational, not error/warning noise.

#### `P1` Incremental backup UX improvements
- [ ] `VS-1520` Terminology cleanup (`Full`, `Incremental`, `Imported`).
  - Scope: unify labels across Dashboard/Projects/Backups/restore dialogs.
  - Depends on: none.
  - Acceptance tests:
    - UI: no conflicting legacy terms remain in primary flows.
    - Localization: new keys exist for all supported languages.
- [ ] `VS-1521` Retention outcome surfacing in history/details.
  - Scope: show what retention will do or did for the selected backup entry.
  - Depends on: `VS-1520`.
  - Acceptance tests:
    - UI: retention outcome line appears for full/incremental/imported entries.
    - Integration: values align with actual retention engine decisions.
- [ ] `VS-1522` Restore guidance block by backup type.
  - Scope: show "what happens next" guidance before confirmation.
  - Depends on: `VS-1520`.
  - Acceptance tests:
    - UI: guidance changes correctly with selected backup type.
    - UX check: keyboard navigation reaches guidance and actions cleanly.
- [ ] `VS-1523` Documentation and help parity.
  - Scope: README/wiki/help text updated to match final terminology and restore guidance.
  - Depends on: `VS-1520`, `VS-1521`, `VS-1522`.
  - Acceptance tests:
    - Docs: screenshots and terminology match shipped UI.
    - Support check: troubleshooting references updated terms only.

#### `P2` Snapshot diff summaries
- [ ] `VS-1530` Compute + persist diff summary statistics.
  - Scope: added/modified/deleted counts, top changed paths, net size delta.
  - Depends on: none.
  - Acceptance tests:
    - Unit: summary math is correct for synthetic change sets.
    - Perf: summary calculation does not introduce noticeable UI blocking.
- [ ] `VS-1531` Projects/Backups summary panel.
  - Scope: compact diff summary UI with concise labels and fallback states.
  - Depends on: `VS-1530`.
  - Acceptance tests:
    - UI: summary panel renders correctly for empty, small, and large diffs.
    - UI quality: no clipping in common windowed sizes.
- [ ] `VS-1532` Export summary action (text/JSON).
  - Scope: export per-snapshot summary for sharing/troubleshooting.
  - Depends on: `VS-1530`.
  - Acceptance tests:
    - Integration: exported file matches on-screen summary values.
    - Regression: export failure path shows actionable error without crashing flow.

#### Stabilization + release gate tickets
- [ ] `VS-1590` Performance and UI-thread hardening.
  - Scope: tune defaults and remove hotspots introduced by new `1.5` flows.
  - Acceptance tests:
    - Benchmark: startup and backup path remain at or better than `1.4` baseline.
    - QA: no blocking UI regressions in backup/restore/settings flows.
- [ ] `VS-1591` Compatibility matrix validation (`1.4` <-> `1.5`).
  - Scope: mixed-version metadata sync, encrypted/plain coexistence, import/export behavior.
  - Acceptance tests:
    - Matrix run: pass on all supported mixed-version scenarios.
    - Regression: no sync-state corruption or tombstone merge regressions.
- [ ] `VS-1592` Localization, docs, and release readiness.
  - Scope: complete localization coverage, release notes, troubleshooting updates.
  - Acceptance tests:
    - Localization: all new `1.5` keys present across supported language files.
    - Release checklist: docs and troubleshooting pages updated and reviewed.

### 1.5 release execution plan (how we tackle it)
1. Phase `A` (security backbone): complete `VS-1501` -> `VS-1504` -> `VS-1502` -> `VS-1503` -> `VS-1505` before feature freeze.
2. Phase `B` (operational controls): deliver `VS-1510` -> `VS-1511` -> `VS-1512` -> `VS-1513` with visible policy state in cards/tray/logs.
3. Phase `C` (clarity and insights): run `VS-1520`/`VS-1521`/`VS-1522` in parallel with `VS-1530`, then close with `VS-1523`, `VS-1531`, `VS-1532`.
4. Phase `D` (stabilization): execute `VS-1590`, `VS-1591`, `VS-1592` and block release until all exit gates pass.
5. Weekly operating rhythm:
   - Start-of-week: lock ticket scope and dependency order.
   - Mid-week: integration checkpoint on mixed-version sync + backup/restore regressions.
   - End-of-week: demo + hardening triage + release-gate burn-down.
6. Release gate policy:
   - No unresolved `P0` or compatibility defects.
   - No known data-loss/corruption path.
   - Localization/docs complete for all shipped `1.5` UX.

### 1.5 stabilization pass
- [ ] `P0` Post-feature hardening.
  - Tune defaults.
  - Reduce UI-thread churn in new flows.
  - Close regressions before 1.6 work starts.
  - Exit gate checklist:
    - No blocking UI regressions in backup/restore/settings flows.
    - Startup impact unchanged or improved versus 1.4 baseline.
    - Localization coverage complete for all new `1.5` strings.
    - Metadata sync compatibility verified across mixed 1.4/1.5 machines.
    - Release docs and troubleshooting updated.

### 1.5 risks
- Crypto UX risk: password loss/typo can make backups unusable without clear recovery messaging.
- Performance risk: encryption and diff summaries can increase backup time on slower disks.
- Compatibility risk: metadata sync across versions must preserve mixed encrypted/plain behavior.
- Support risk: quiet-hours/throttling can appear as random pauses if status is not clear.

## 1.6.x
- [ ] `P1` Richer restore flows (selective restore, dry-run previews, conflict prompts).
- [ ] `P1` Restore point browser with compare + timeline.
- [ ] `P1` Smarter storage usage reporting (per-project deltas, change summaries).
- [ ] `P2` Custom preset editor for filter/ignore rules.
- [ ] `P2` Backup health timeline (success/failure trends).
- [ ] `P2` Exportable config bundle for migration/support.

## 1.7.x
- [ ] `P1` Project tagging + bulk actions (pause/backup/snapshot by tag).
- [ ] `P1` Per-destination retry policy with backoff + user status summary.
- [ ] `P2` Destination quotas + cleanup suggestions.
- [ ] `P2` Team workflows (shared vaults, access control, audit trails).

## Long-term
- [ ] Multi-destination health scoring and auto-failover.
- [ ] Cloud targets (S3-compatible, Backblaze, etc.) with encryption.
- [ ] Automation hooks (webhooks/scripts on backup/restore events).
- [ ] CLI parity with all major UI features.
- [ ] Per-project verification policies (always/scheduled/manual).
- [ ] App signing for trusted distribution.
- [ ] Background integrity audits with alerts.
