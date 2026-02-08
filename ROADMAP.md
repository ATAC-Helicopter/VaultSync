# Roadmap

## Priority legend
- `P0` Critical: core reliability/security and release blockers.
- `P1` High: major UX/product impact for the target release.
- `P2` Medium: valuable but can slip without harming release quality.

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
