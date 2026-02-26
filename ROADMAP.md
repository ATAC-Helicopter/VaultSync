# Roadmap

## Priority legend
- `P0` Critical: core reliability/security and release blockers.
- `P1` High: major UX/product impact for the target release.
- `P2` Medium: valuable but can slip without harming release quality.

## Planning convention
- Use `VS-xxxx` IDs as the default planning unit for roadmap items, implementation tasks, and release work.
- Pre-`2.0` planning uses explicit `1.x` release streams (for example: `1.5.x`, `1.6.x`, `1.7.x`, `1.8.x`, `1.9.x`) instead of a generic long-term bucket.
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

## Roadmap sync format (project automation)
- Keep execution tickets in this canonical format so scripts can sync to GitHub Project reliably:
  - `- [ ] \`VS-xxxx\` Title`
  - Optional details/acceptance criteria stay as indented bullets below the ticket.
- Use release sections as routing signals:
  - `## 1.5.x`, `## 1.6.x`, `## 1.7.x`, `## 1.8.x`, `## 1.9.x`
- For new work, always include:
  - ID (`VS-xxxx`)
  - Priority marker (`P0/P1/P2`) at the start of the ticket line when relevant.
  - Clear one-line scope so title can become an issue/project item title without rewriting.

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
- [x] `P0` Backup encryption and password-protected backups.
- [x] `P1` Backup bandwidth limits and quiet hours.
- [x] `P1` Incremental backup UX improvements.
- [x] `P2` Snapshot diff summaries.

### Current status (as of 2026-02-25)
- `1.5` feature scope is functionally complete (`P0` + `P1` + `P2` done).
- Recent UI stabilization pass completed for:
  - Dashboard weekly analytics card redesign + data-binding correctness fixes.
  - Dashboard storage donut layout/fallback improvements and tooltip overflow hardening.
  - Backups/Projects windowed-layout alignment fixes (header/action spacing, chart overlap, avatar/title row alignment).
  - Settings wording polish (English keys) for quiet-hours and destination controls.
- Remaining release-gate work:
  - `VS-1590` Performance and UI-thread hardening.
  - `VS-1591` Compatibility matrix validation (`1.4` <-> `1.5`).
  - `VS-1592` Localization/docs/release readiness final pass.

### 1.5.1 stabilization backlog (bugs, glitches, optimization)
- [x] `P0` `VS-1565` Remove `async void` from backup/history runtime handlers and route through `Task` + centralized exception/log handling.
  - Scope: `AppViewModel.*` backup/history/runtime/tray handlers and `BackupsViewModel` export action.
  - Acceptance: no unobserved task exceptions; no behavior regressions in backup/restore/delete/open-folder flows.
  - Current status:
    - Done: backup/history/runtime handler entry points now use `void` wrappers with `Task` implementations and centralized detached-operation exception logging.
    - Done: Backups diff export action no longer uses `async void`.
    - Done: tray open-folder + lock-now handlers and project/settings handlers now run through detached `Task` wrappers.
    - Done: no `async void` handlers remain in `src/` (checked via repository scan).
- [ ] `P1` `VS-1566` Backups history performance pass: reduce full-list/group rebuild churn on filter and refresh updates.
  - Scope: optimize `BackupsViewModel.RefreshSnapshotsView` / grouping path, avoid unnecessary collection clears/rebuilds.
  - Acceptance: smoother filter toggles and lower UI thread time on large history sets.
  - Current status:
    - In progress: group rebuild now uses a cached project lookup map instead of rebuilding dictionary/group mappings each refresh.
    - In progress: snapshot metadata lookup now queries only required snapshot IDs (instead of loading all snapshots) when shaping backup history cards.
- [x] `P1` `VS-1567` Convert hot repository reads from `Task.Run` wrappers to true async DB calls.
  - Scope: `SqliteRepository` high-frequency read APIs used by Dashboard/Projects/Backups.
  - Acceptance: reduced thread-pool pressure during refresh/auto-refresh; no query-result regressions.
  - Current status:
    - Done: `SqliteRepository` async read helpers for projects/snapshots/files/backups now use Dapper async query APIs (`CommandDefinition` + cancellation tokens) instead of `Task.Run` wrappers.
- [ ] `P1` `VS-1568` macOS notification icon parity: use VaultSync app icon instead of default Avalonia icon.
  - Scope: `MacSystemNotificationService` notification payload/icon path wiring.
  - Acceptance: macOS system notifications display VaultSync branding icon consistently.
  - Current status:
    - In progress: macOS notifications now prefer `terminal-notifier` (when installed) and pass VaultSync icon path; fallback remains AppleScript notification.
- [ ] `P2` `VS-1569` Replace blocking retry sleeps with async backoff in config/metadata I/O paths.
  - Scope: `AppConfigStore`, `MetadataStore`, `MetadataSyncService`.
  - Acceptance: cancellation-aware retries; no UI stalls from blocking waits.
  - Current status:
    - In progress: `AppConfigStore` now provides `SaveAsync` with cancellation-aware backoff (`Task.Delay`) and Settings async save path now uses it.
    - In progress: metadata sync import/preview/export/tombstone flows now have async APIs, use `SemaphoreSlim.WaitAsync`, and use cancellation-aware `Task.Delay` backoff in retry loops.
- [x] `P2` `VS-1574` Metadata schema migration guardrails for idempotent startup.
  - Scope: `MetadataStore.EnsureSchema` duplicate-column migration behavior on existing stores.
  - Acceptance: startup schema checks are idempotent and do not throw duplicate-column SQLite exceptions during debug runs.
  - Current status:
    - Done: column migrations now use `PRAGMA table_info(...)` presence checks before `ALTER TABLE`.
    - Done: avoids first-chance `duplicate column name` exceptions on already-migrated metadata stores.
- [x] `P2` `VS-1575` Drive health probe executable resolution hardening on Windows.
  - Scope: `DriveHealthService` process-launch path resolution for `smartctl` and other external probes.
  - Acceptance: manual backup start does not throw first-chance `Win32Exception` when `smartctl` is not installed; missing probe tools degrade to `Unknown` cleanly.
  - Current status:
    - Done: process runner now resolves executable paths from `PATH` (and `PATHEXT` on Windows) before `Process.Start`.
    - Done: `smartctl` path resolution now returns empty when unavailable instead of attempting direct launch.
- [x] `P2` `VS-1576` Network-drive path guardrails for SMB/UNC detection.
  - Scope: `AppViewModel.IsNetworkDrivePath` path-root validation before `DriveInfo` construction.
  - Acceptance: no `ArgumentException` on UNC roots (`\\\\server\\share`) during manual backup startup path checks.
  - Current status:
    - Done: UNC paths are now treated as network upfront.
    - Done: `DriveInfo` is now invoked only for drive-letter roots (`C:` / `C:\\`), avoiding invalid-root exceptions.
- [x] `P2` `VS-1577` Archive upload auto-tune timeout cancellation noise cleanup.
  - Scope: `AppViewModel.RuntimeOps` archive probe timeout handling (`ProbeArchiveUploadBufferBytes` / `EnsureArchiveUploadBufferAsync`).
  - Acceptance: timeout-based fallback does not throw `OperationCanceledException` during normal debug runs; explicit user cancellation still cancels backup flow.
  - Current status:
    - Done: timeout and user-cancel tokens are now handled separately in archive probe flow.
    - Done: timeout path now returns a timed-out probe result and falls back cleanly without raising cancellation exceptions.
- [x] `P2` `VS-1578` App config read retry on transient file-lock contention.
  - Scope: `AppConfigStore.Load` config read path while export/save routines may hold a short file lock.
  - Acceptance: transient lock on `appsettings.json` no longer causes immediate read failure in debug/runtime hot paths.
  - Current status:
    - Done: config reads now use shared-read stream mode with retry backoff.
    - Done: fallback behavior remains unchanged for unrecoverable read errors.
- [x] `P1` `VS-1570` Adaptive archive compression policy tuning.
  - Scope: archive backup creation path in `BackupService`.
  - Acceptance: keep backup/restore format compatibility while improving speed/ratio balance by file type.
  - Current status:
    - Done: archive compression now selects per-file level (`NoCompression` for already-compressed/media, `Optimal` for text/code, `Fastest` fallback).
    - Done: full solution build + core tests pass after change.
- [x] `P2` `VS-1571` README feature/status refresh and screenshot placeholders.
  - Scope: top-level repo README content and screenshot scaffolding.
  - Acceptance: README reflects current 1.5.x capabilities and has clear placeholder locations for app screenshots.
  - Current status:
    - Done: outdated feature wording refreshed for current 1.5.x behavior.
    - Done: placeholder SVGs added for Dashboard/Projects/Backups/Settings screenshots under `docs/images/placeholders/`.
- [x] `P2` `VS-1572` Consumer-friendly preset catalog + preset guidance in Projects UI.
  - Scope: add consumer presets (Photos/Documents/Steam mods/Creative suites), harden preset file-resolution via index mapping, and show preset description/examples in the Projects card.
  - Acceptance: preset IDs can safely differ from file names, new presets appear in selector, and selected preset guidance text is visible in-app/docs.
  - Current status:
    - Done: added new preset definitions/files (`photos`, `documents`, `steam_mods`, `creative_suite`) with descriptions/examples in `presets.index.json`.
    - Done: Core preset resolution paths now use index-aware fallback when `id` != file stem.
    - Done: Projects UI now renders selected preset description + example hint; docs updated.
- [x] `P1` `VS-1573` Projects action-state reliability and notification cancellation noise cleanup.
  - Scope: keep project action buttons (`Open folder`, `Remove from VaultSync`) in sync with selection state and treat auto-dismiss cancellation as expected in notification flows.
  - Acceptance: action buttons no longer remain incorrectly disabled after selection/state changes; no debug-noise cancellation exceptions from notification auto-dismiss during normal UI activity.
  - Current status:
    - Done: project action commands now raise `CanExecuteChanged` when `SelectedProject` changes, preventing stale disabled state.
    - Done: notification auto-dismiss now uses race-safe CTS replacement/disposal and handles `OperationCanceledException` as normal superseded-flow behavior.
    - Done: Projects detail row layout corrected so controls/stats do not overlap and hide preset/destination/encryption controls.

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
  - Status:
    - Feature implementation is complete (`VS-1501`..`VS-1505`, `VS-1530`..`VS-1539`, `VS-1543`).
    - Remaining release-gate validation is tracked under `VS-1591` (compatibility matrix) and `VS-1592` (localization/docs readiness).

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
3. Encryption controls and key management UX (global + per-project).
4. Bandwidth + quiet-hours policy.
5. Incremental UX clarity pass.
6. Snapshot diff summaries.
7. Stabilization pass + release gate.

### 1.5 ticket backlog (execution-ready)

#### `P0` Encryption and password-protected backups
- [x] `VS-1501` Crypto format + metadata contract (schema/versioning).
  - Scope: define encrypted container descriptor, algorithm/KDF parameter identifiers, and migration-safe format versioning.
  - Depends on: none.
  - Acceptance tests:
    - Unit: descriptor serialize/deserialize round-trip with version field preserved.
    - Unit: metadata export payload includes only non-secret crypto fields.
    - Integration: existing plain backup metadata still parses unchanged.
- [x] `VS-1502` Encrypted write pipeline.
  - Scope: produce encrypted backup artifacts (AES-256 + per-backup salt/IV) in vault storage path.
  - Depends on: `VS-1501`.
  - Acceptance tests:
    - Integration: encrypted backup artifact differs from plaintext source and cannot be opened as plain archive.
    - Integration: backup job reports success and emits encrypted flag in metadata.
    - Regression: plain (unencrypted) backup flow remains unchanged.
- [x] `VS-1503` Password-gated restore/decrypt pipeline.
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
- [x] `VS-1505` Mixed encrypted/plain interop + metadata sync compatibility.
  - Scope: preserve merge/tombstone/import behavior across mixed `1.4` and `1.5` machines.
  - Depends on: `VS-1501`, `VS-1502`, `VS-1503`.
  - Acceptance tests:
    - Integration: import/export round-trip with mixed encrypted/plain history works without data loss.
    - Integration: `1.4` client ignores unknown crypto descriptors without corrupting sync state.
    - Regression: delete/keep/retention/destination scan/import behavior remains stable.
- [x] `VS-1530` Global encryption settings UX + secure secret enrollment.
  - Scope: add Settings UI for global encryption enable/disable, password set/change, and secure-store enrollment using non-secret config refs only.
  - Depends on: `VS-1504`.
  - Acceptance tests:
    - UI: user can enable/disable global encryption and set/change password without storing plaintext in config.
    - Unit: encrypted backup run fails fast with actionable error when global encryption is enabled but secret is unavailable.
    - Regression: existing backup settings persist unchanged.
- [x] `VS-1531` Per-project encryption policy controls.
  - Scope: add per-project toggle and policy mode (`inherit global`, `project encrypted`, `project plain`) with clear effective-state display.
  - Depends on: `VS-1530`.
  - Acceptance tests:
    - UI: per-project policy can be changed and persists across restart.
    - Integration: effective policy precedence works (`project override` > `global`).
    - Regression: auto-backup per-project toggle behavior remains unchanged.
- [x] `VS-1532` Per-project key reference model + migration.
  - Scope: persist per-project encryption mode and optional project `KeyRef` in DB/config with migration-safe defaults.
  - Depends on: `VS-1531`.
  - Acceptance tests:
    - Migration: existing project rows load with `inherit` defaults and no data loss.
    - Unit: model serialization/persistence stores only key references (no secret material).
    - Integration: import/export keeps non-secret encryption policy fields stable.
- [x] `VS-1533` Backup pipeline effective-key resolution.
  - Scope: resolve encryption mode and key source per project at backup runtime (global key, project key, or plain).
  - Depends on: `VS-1532`.
  - Acceptance tests:
    - Integration: global encrypted + project plain produces plain backup for overridden project only.
    - Integration: global plain + project encrypted produces encrypted backup for overridden project only.
    - Regression: existing encrypted backup metadata contract (`is_encrypted`, descriptor JSON) remains unchanged.
- [x] `VS-1534` Restore key resolution and prompt fallback.
  - Scope: restore flow resolves project key first, then global key, and prompts only when required.
  - Depends on: `VS-1532`, `VS-1533`.
  - Acceptance tests:
    - Integration: encrypted restore succeeds without prompt when matching key exists in secure store.
    - Integration: missing key triggers prompt and succeeds with correct password.
    - Integration: wrong password fails safely without partial writes.
- [x] `VS-1535` Explorer `.vse` open flow (password dialog helper).
  - Scope: register/handle encrypted artifact open action so opening `.vse` launches a minimal VaultSync dialog for password + temp extraction/open.
  - Depends on: `VS-1534`.
  - Acceptance tests:
    - Integration: opening `.vse` triggers password dialog and opens extracted temp folder on success.
    - Integration: wrong password shows explicit error and leaves no partial extracted data.
    - Regression: standard “open backup folder” behavior remains unchanged.
- [x] `VS-1536` Existing-backup key rotation job.
  - Scope: explicit user-triggered re-encryption of existing encrypted backups from old key to new key (project or global scope) with atomic replacement.
  - Depends on: `VS-1533`, `VS-1534`.
  - Acceptance tests:
    - Integration: rotate succeeds for selected backups and old password no longer decrypts rotated artifacts.
    - Integration: interruption/failure leaves original backup intact (no corruption).
    - UX: per-backup failure summary lists skipped/failed/succeeded entries.
- [x] `VS-1537` Per-project password management in Projects + Backups pages.
  - Scope: expose per-project password set/clear flow in both pages using one shared app-level handler and one persisted `encryption_key_ref` source of truth.
  - Depends on: `VS-1531`, `VS-1532`, `VS-1533`.
  - Acceptance tests:
    - UI: setting/clearing a project encryption password from Projects is reflected in Backups without drift.
    - UI: setting/clearing from Backups is reflected in Projects without drift.
    - Regression: policy updates (`inherit/encrypted/plain`) preserve existing key reference and do not desync between pages.
- [x] `VS-1538` Encrypted `Open folder` unlock entry flow.
  - Scope: clicking `Open folder` on encrypted backups runs password/key resolution, decrypts into a temp workspace, and opens that decrypted workspace directly.
  - Depends on: `VS-1534`, `VS-1535`.
  - Acceptance tests:
    - Integration: encrypted `Open folder` opens decrypted temp workspace after valid password/key.
    - Integration: invalid password shows explicit error and creates no partial workspace.
    - Regression: plain `Open folder` keeps current behavior unchanged.
- [x] `VS-1539` Encrypted `Open folder` lock lifecycle + cleanup hardening.
  - Scope: add lock/cleanup lifecycle for decrypted temp workspaces (explicit lock action, timeout auto-lock, startup stale cleanup, safe crash recovery path).
  - Depends on: `VS-1538`.
  - Plain-language behavior:
    - When an encrypted backup is opened, VaultSync decrypts it to a temp workspace only.
    - User can force-close that decrypted workspace with `Lock now`.
    - If the user does nothing, VaultSync auto-locks (deletes temp decrypted data) after a timeout.
    - On app restart/crash recovery, stale decrypted temp folders are cleaned automatically.
  - Integration contract:
    - Encrypted backups never expose decrypted files in destination roots.
    - Decrypted temp workspaces are never persisted in metadata/config.
    - Metadata sync/export/import remains unchanged (no secret material, no decrypted-path persistence).
  - Acceptance tests:
    - Integration: temp workspace is removed on lock/timeout/restart.
    - Integration: stale workspace cleanup runs on app startup.
    - Regression: restore and metadata sync behavior unchanged.
  - Current status:
    - Done: startup stale temp cleanup, timed auto-cleanup for decrypted open-folder staging roots, explicit `Lock now` action, and shared configurable timeout for in-app + external `.vse` open flows.
- [x] `VS-1543` Session unlock cache + timed auto-relock for encrypted open flow.
  - Scope: introduce a per-project encrypted-open session unlock cache so repeated `Open folder` actions within a configured timeout do not re-prompt for password.
  - Depends on: `VS-1538`, `VS-1539`.
  - Plain-language behavior:
    - First encrypted open asks for password.
    - Additional opens for the same project within the unlock window do not ask again.
    - Once timeout expires (or user clicks `Lock now`), password is required again.
    - This cache is memory-only for the current app session (never written to config/metadata).
  - Integration contract:
    - Session unlock is memory-only (no plaintext secrets persisted to config/metadata).
    - Unlock timeout is configurable in Settings and auto-relocks on expiry.
    - Lock state controls access checks (not temp folder lifetime): decrypted workspace handling remains governed by `VS-1539` cleanup/lock rules.
    - Manual `Lock now` invalidates active session unlock immediately.
  - Acceptance tests:
    - Integration: first encrypted open prompts for password; repeated open within timeout does not.
    - Integration: after timeout expiry, password prompt is required again.
    - Integration: `Lock now` forces prompt on next open even before timeout.
    - Regression: metadata sync/export/import and plain backup open flow are unchanged.

#### `P1` Bandwidth limits and quiet hours
- [x] `VS-1510` Config model + settings UI for caps and schedule.
  - Scope: settings schema, validation, timezone-aware quiet-hours range editor.
  - Depends on: none.
  - Acceptance tests:
    - Unit: invalid caps/schedules are rejected with actionable validation messages.
    - UI: settings persist and reload accurately across restart.
  - Current status:
    - Done: config schema fields for bandwidth caps + quiet-hours and Settings UI controls with validation/persistence.
- [x] `VS-1511` Transfer throttling enforcement.
  - Scope: apply effective bandwidth cap to archive upload/network copy workers.
  - Depends on: `VS-1510`.
  - Acceptance tests:
    - Integration: measured throughput stays within configured cap tolerance.
    - Regression: no cap configured preserves current throughput behavior.
  - Current status:
    - Done: native copy runners now apply configured bandwidth caps (`rsync --bwlimit`, robocopy `/IPG`) via shared transfer policy math.
- [x] `VS-1512` Quiet-hours runtime policy engine.
  - Scope: defer/pause/start rules based on local time and running backup state.
  - Depends on: `VS-1510`.
  - Acceptance tests:
    - Integration: backup start during quiet hours follows configured policy deterministically.
    - Integration: crossing quiet-hours boundary transitions active jobs predictably.
  - Current status:
    - Done: auto-backup timer runs now evaluate quiet-hours policy before preflight and skip deterministically during the configured window.
    - Done: active backup runs are not force-cancelled when quiet-hours begins; policy applies to new auto-backup starts only.
    - Done: shared `QuietHoursPolicy` helper + automated unit coverage for overnight/daytime windows and invalid-time fallback.
- [x] `VS-1513` Policy visibility in cards, tray, and logs.
  - Scope: expose effective policy state (`Throttled`, `Quiet hours`) in UI and operational logs.
  - Depends on: `VS-1511`, `VS-1512`.
  - Acceptance tests:
    - UI: active cards and tray always show current policy state when applicable.
    - Log check: policy transition logs are informational, not error/warning noise.
  - Current status:
    - Done: active backup cards (Backups + backup widget) now show a policy chip when throttling/quiet-hours policy is active.
    - Done: tray native menu + tray panel summary now include current policy state.
    - Done: backup policy transitions are logged as informational `[Policy]` entries and trigger tray refresh on change.

#### `P1` Incremental backup UX improvements
- [x] `VS-1520` Terminology cleanup (`Full`, `Incremental`, `Imported`).
  - Scope: unify labels across Dashboard/Projects/Backups/restore dialogs.
  - Depends on: none.
  - Acceptance tests:
    - UI: no conflicting legacy terms remain in primary flows.
    - Localization: new keys exist for all supported languages.
  - Current status:
    - Done: backup records now persist `backup_mode` (`full` / `incremental`) so terminology is rendered from real per-backup data.
    - Done: Backups history cards now use `Full` / `Incremental` / `Imported` terminology based on stored mode + imported state.
- [x] `VS-1521` Retention outcome surfacing in history/details.
  - Scope: show what retention will do or did for the selected backup entry.
  - Depends on: `VS-1520`.
  - Acceptance tests:
    - UI: retention outcome line appears for full/incremental/imported entries.
    - Integration: values align with actual retention engine decisions.
  - Current status:
    - Done: Backups history cards now render a retention outcome line (`eligible`, `protected`, `imported history`) and update live when Keep is toggled.
- [x] `VS-1522` Restore guidance block by backup type.
  - Scope: show "what happens next" guidance before confirmation.
  - Depends on: `VS-1520`.
  - Acceptance tests:
    - UI: guidance changes correctly with selected backup type.
    - UX check: keyboard navigation reaches guidance and actions cleanly.
  - Current status:
    - Done: restore flow now shows a confirmation dialog with a "What happens next" guidance block before restore starts.
    - Done: guidance content is type-aware (`Full` / `Incremental` / `Imported`) and encryption-aware.
- [x] `VS-1523` Documentation and help parity.
  - Scope: README/wiki/help text updated to match final terminology and restore guidance.
  - Depends on: `VS-1520`, `VS-1521`, `VS-1522`.
  - Acceptance tests:
    - Docs: screenshots and terminology match shipped UI.
    - Support check: troubleshooting references updated terms only.
  - Current status:
    - Done: README and docs/wiki pages now include backup-type terminology (`Full`, `Incremental`, `Imported`) and restore-guidance confirmation notes.

#### `P2` Snapshot diff summaries
- [x] `VS-1540` Compute + persist diff summary statistics.
  - Scope: added/modified/deleted counts, top changed paths, net size delta.
  - Depends on: none.
  - Acceptance tests:
    - Unit: summary math is correct for synthetic change sets.
    - Perf: summary calculation does not introduce noticeable UI blocking.
  - Current status:
    - Done: snapshot creation now computes and persists diff counts, top-changed path stats, and net size delta in local DB schema.
    - Done: metadata export/import for snapshots now carries diff summary fields with backward-compatible defaults for older metadata stores.
    - Done: automated tests cover repository persistence, snapshot-service summary math, and metadata-sync summary round-trip import.
- [x] `VS-1541` Projects/Backups summary panel.
  - Scope: compact diff summary UI with concise labels and fallback states.
  - Depends on: `VS-1540`.
  - Acceptance tests:
    - UI: summary panel renders correctly for empty, small, and large diffs.
    - UI quality: no clipping in common windowed sizes.
  - Current status:
    - Done: Projects recent snapshot cards now show compact diff summary lines (+/~/- with signed net delta) and optional top-path preview.
    - Done: Backups history cards now render per-snapshot diff summary and top changed paths (when present), with fallback text for no-change/unavailable states.
    - Done: layout was reflowed to keep summary content inside card bounds in windowed mode.
- [x] `VS-1542` Export summary action (text/JSON).
  - Scope: export per-snapshot summary for sharing/troubleshooting.
  - Depends on: `VS-1540`.
  - Acceptance tests:
    - Integration: exported file matches on-screen summary values.
    - Regression: export failure path shows actionable error without crashing flow.
  - Current status:
    - Done: Backups history cards now include export actions for text and JSON snapshot diff summaries.
    - Done: exports are written under `Documents/VaultSync/Exports/SnapshotDiff` with collision-safe filenames and user-facing success/failure notifications.
    - Done: Backups history cards now include an in-app git-style diff preview dialog for per-snapshot inspection before export/share.

#### Stabilization + release gate tickets
- [x] `VS-1590` Performance and UI-thread hardening.
  - Scope: tune defaults and remove hotspots introduced by new `1.5` flows.
  - Acceptance tests:
    - Benchmark: startup and backup path remain at or better than `1.4` baseline.
    - QA: no blocking UI regressions in backup/restore/settings flows.
- [x] `VS-1591` Compatibility matrix validation (`1.4` <-> `1.5`).
  - Scope: mixed-version metadata sync, encrypted/plain coexistence, import/export behavior.
  - Acceptance tests:
    - Matrix run: pass on all supported mixed-version scenarios.
    - Regression: no sync-state corruption or tombstone merge regressions.
  - Current status:
    - Done: compatibility runbook + case matrix drafted (`CM-1501`..`CM-1508`).
    - Done: automated core-suite execution recorded (`65/65` passing) with matrix-to-test evidence mapping.
    - Pending: execute remaining manual mixed-client cases (`CM-1502`, `CM-1507`, `CM-1508`) on real `1.4.x` and `1.5.x` binaries.
- [x] `VS-1592` Localization, docs, and release readiness.
  - Scope: complete localization coverage, release notes, troubleshooting updates.
  - Acceptance tests:
    - Localization: all new `1.5` keys present across supported language files.
    - Release checklist: docs and troubleshooting pages updated and reviewed.

### 1.5 release execution plan (how we tackle it)
1. Phase `A` (security backbone): complete `VS-1501` -> `VS-1504` -> `VS-1502` -> `VS-1503` -> `VS-1505` before feature freeze.
2. Phase `B` (encryption controls + usability): deliver `VS-1530` -> `VS-1531` -> `VS-1532` -> `VS-1533` -> `VS-1534`, then `VS-1535`, `VS-1536`, `VS-1538`, `VS-1539`, `VS-1543`.
3. Phase `C` (operational controls): deliver `VS-1510` -> `VS-1511` -> `VS-1512` -> `VS-1513` with visible policy state in cards/tray/logs.
4. Phase `D` (clarity and insights): run `VS-1520`/`VS-1521`/`VS-1522` in parallel with `VS-1540`, then close with `VS-1523`, `VS-1541`, `VS-1542`.
5. Phase `E` (stabilization): execute `VS-1590`, `VS-1591`, `VS-1592` and block release until all exit gates pass.
6. Weekly operating rhythm:
   - Start-of-week: lock ticket scope and dependency order.
   - Mid-week: integration checkpoint on mixed-version sync + backup/restore regressions.
   - End-of-week: demo + hardening triage + release-gate burn-down.
7. Release gate policy:
   - No unresolved `P0` or compatibility defects.
   - No known data-loss/corruption path.
   - Localization/docs complete for all shipped `1.5` UX.

### 1.5 stabilization pass
- [x] `P0` Post-feature hardening.
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
- [ ] `VS-1601` `P1` Richer restore flows (selective restore, dry-run previews, conflict prompts).
- [ ] `VS-1602` `P1` Restore point browser with compare + timeline.
- [ ] `VS-1603` `P1` Smarter storage usage reporting (per-project deltas, change summaries).
- [ ] `VS-1604` `P2` Custom preset editor for filter/ignore rules.
- [ ] `VS-1605` `P2` Backup health timeline (success/failure trends).
- [ ] `VS-1606` `P2` Exportable config bundle for migration/support.

## 1.7.x
- [ ] `VS-1701` `P1` Project tagging + bulk actions (pause/backup/snapshot by tag).
- [ ] `VS-1702` `P1` Per-destination retry policy with backoff + user status summary.
- [ ] `VS-1703` `P2` Destination quotas + cleanup suggestions.
- [ ] `VS-1704` `P2` Team workflows (shared vaults, access control, audit trails).

## 1.8.x
- [ ] `VS-1801` `P1` Multi-destination health scoring and auto-failover.
- [ ] `VS-1802` `P1` Cloud targets (S3-compatible, Backblaze, etc.) with encryption.
- [ ] `VS-1803` `P2` Automation hooks (webhooks/scripts on backup/restore events).
- [ ] `VS-1804` `P2` CLI parity with all major UI features.

## 1.9.x
- [ ] `VS-1901` `P1` Per-project verification policies (always/scheduled/manual).
- [ ] `VS-1902` `P1` App signing for trusted distribution.
- [ ] `VS-1903` `P2` Background integrity audits with alerts.
