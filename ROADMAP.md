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
- [x] `P2` `VS-1579` Backup delete resilience and debug-noise cleanup for tool/runtime probes.
  - Scope: backup history robust delete flow, diagnostics dump-tool startup guard, and projects dropdown selection stability during option refresh.
  - Acceptance: delete flow handles protected marker files without throwing UI-level exceptions; missing `dotnet-dump` no longer throws process-start exceptions; destination/encryption dropdowns do not collapse to blank transient null state.
  - Current status:
    - Done: robust directory delete now clears read-only only when needed and returns failure details without rethrowing.
    - Done: diagnostics dump collection now checks `dotnet-dump` availability before process launch and logs a skip when unavailable.
    - Done: projects destination selection now ignores transient null selection events during options source refresh.
- [x] `P0` `VS-1581` Windows elevated patch-helper argument parsing fix.
  - Scope: `PatchInstallService` helper launch/parsing for Program Files installs that require elevation.
  - Acceptance: elevated patch install keeps `InstallDir` clean, preserves `--restart` / `--waitpid`, and applies patches successfully on installed Windows builds.
  - Current status:
    - Done: helper launch now writes a patch-apply request file and passes only `--apply-patch-request <file>` through elevation.
    - Done: restart and wait-pid settings are now loaded from the request payload instead of fragile elevated command-line quoting.
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
    - Regression: standard �open backup folder� behavior remains unchanged.
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

### 1.6.0 direction
- Release target: **2026-03-09** (Monday).
- Ship rule: only critical fixes and already in-progress `1.6.0` work should be pulled into this release window; all non-critical scope rolls forward.
- Theme: control, restore safety, and organization.
- Product goal: make VaultSync feel safer to restore from, easier to organize at scale, and more user-controlled in everyday project setup.
- Release shape:
  - Restore confidence: safer/clearer restore planning and optional sandbox-first restore.
  - Preset ownership: full preset editor instead of file-only preset maintenance.
  - Project organization: tags, smart groups, and bulk operations for larger vaults.

### 1.6.0 priorities
- [ ] `P0` Restore planning and sandbox-first restore workflow.
- [ ] `P0` Full preset editor with live preview and project assignment.
- [ ] `P1` Project tags, smart groups, and bulk actions.
- [ ] `P1` Verification and storage insight upgrades.

### 1.6.0 scope and contracts

#### `P0` Restore planning and sandbox-first restore
- Core scope:
  - Optional sandbox restore mode with per-project default and per-run override.
  - Restore preview before commit (overwrite, add, delete, size impact, conflict list).
  - Promote sandbox contents into final destination only after explicit confirmation.
  - Cleanup/retry controls for sandbox workspaces.
- UX contract:
  - Direct restore remains supported and stays the default unless user/project chooses sandbox-first.
  - Per-project restore mode can be configured, but restore dialog can override for the current run.
  - Restore summary must clearly distinguish preview-only, sandboxed, and final-apply stages.
- Acceptance:
  - Sandbox restore can be enabled per project and overridden per restore.
  - User can inspect sandbox output before committing into the final location.
  - Canceling before final apply leaves original destination unchanged.

#### `P0` Full preset editor
- Core scope:
  - Create, clone, rename, and delete presets.
  - Edit include/exclude rules in-app.
  - Live match preview against project paths.
  - Import/export preset definitions.
  - Assign a preset to a project directly from the editor.
- UX contract:
  - Built-in presets remain protected or explicitly clone-to-edit.
  - Preview clearly shows matched, ignored, and uncertain paths.
  - Editor works for both technical and consumer-friendly presets.
- Acceptance:
  - User can fully manage custom presets without editing files manually.
  - Preview updates without noticeable UI blocking on common project sizes.
  - Preset changes persist safely and remain compatible with existing preset resolution.

#### `P1` Project organization and grouping
- Core scope:
  - Manual project tags.
  - Smart groups for common states (`Needs backup`, `Encrypted`, `Unprotected`, `Large projects`, `Auto backup off`, `Recently active`).
  - Bulk actions by tag/group (backup, snapshot, pause/resume auto backup where applicable).
  - Shared filtering model between Projects and Backups pages.
- Acceptance:
  - User can organize projects beyond flat list order.
  - Smart groups remain predictable and refresh from real project state.
  - Bulk actions are keyboard-accessible and confirm destructive operations.

#### `P1` Verification and storage insight upgrades
- Core scope:
  - Per-project verification policy controls.
  - Backup health overview (`last backup`, `last verified`, destination state, restore-tested state when available).
  - Storage growth/delta insights by project and destination.
- Acceptance:
  - Verification policy is configurable per project.
  - Health/storage summaries improve backup trust without cluttering core screens.
  - Metrics remain understandable for both technical and non-technical users.

### 1.6 implementation order
1. Restore preview model + sandbox workspace contract.
2. Restore UX flow and confirmation/apply path.
3. Full preset editor with live preview.
4. Tags, smart groups, and shared filtering.
5. Verification policy and storage/health insight surfaces.
6. Stabilization pass and release hardening.

### 1.6 ticket backlog (execution-ready)
- [x] `VS-1601` `P0` Richer restore flows (selective restore, dry-run previews, conflict prompts).
  - Scope: add restore preview model, selective restore targets, and conflict classification before execution.
  - Done:
    - Restore confirmation includes a pre-run preview summary for plain backups (files in backup, new files, overwrite count, potential conflicts, project-only files kept, total bytes).
    - Encrypted backups surface an explicit preview-unavailable reason before decrypt starts.
    - Restore confirmation now supports selective top-level restore targets for plain backups/archives.
    - Restore execution applies only selected top-level targets while preserving direct/sandbox mode behavior.
  - Acceptance tests:
    - UI: restore preview lists overwrite/add/delete/conflict counts before run.
    - Integration: selective restore applies only chosen paths.
    - Regression: standard direct restore still works unchanged when preview options are not used.
- [x] `VS-1602` `P0` Restore point browser with compare + timeline.
  - Scope: timeline-style restore point browser with compare support between selected backups/snapshots.
  - Done:
    - Backups history panel now exposes restore-point timeline selectors (`A` / `B`) bound to filtered chronological history.
    - Added compare action that opens a structured compare summary (time range, elapsed interval, size delta, net-diff delta, and latest-point diff stats).
    - Compare selection works with current history filters and project grouping without changing restore flow behavior.
  - Acceptance tests:
    - UI: user can navigate restore points chronologically and compare two points.
    - UX: browser remains responsive on projects with long history.
- [x] `VS-1603` `P1` Smarter storage usage reporting (per-project deltas, change summaries).
  - Scope: per-project growth metrics, top storage consumers, and clearer dashboard/backups storage summaries.
  - Current status:
    - Done: Backups per-project cards now surface storage delta (`?`) versus the previous backup snapshot size for each project.
    - Done: Backups summary now includes top storage consumers (top projects by local backup storage share).
  - Acceptance tests:
    - UI: metrics align with stored backup/snapshot data.
    - Perf: reporting does not cause blocking UI refresh on common data sets.
- [x] `VS-1604` `P0` Full preset editor with include/exclude rules, preview, clone, import/export.
  - Scope: replace file-only preset maintenance with an in-app editor and preview workflow.
  - Current status:
    - Done: Projects details now includes a preset-rules editor (reload/save) for the selected preset file.
    - Done: Preset editor now includes live preview counts (included/excluded) against the selected project path.
    - Done: Preset editor now supports clone/import/export flows (clone to new preset id, import from file path, export to Documents preset exports).
  - Acceptance tests:
    - UI: user can create/edit/clone/delete/import/export presets.
    - Integration: saved presets are immediately assignable to projects and resolve correctly at snapshot/backup time.
- [x] `VS-1605` `P1` Backup health center and timeline (success/failure/verified trends).
  - Scope: health summary model and timeline surfaces for backup freshness, verification, and failure visibility.
  - Current status:
    - Done: Backups summary now includes a health center mix (healthy/aging/stale/no-backup project distribution) derived from project backup freshness.
  - Acceptance tests:
    - UI: health state reflects real backup/verification history.
    - UX: timeline/trend surfaces do not crowd primary actions.
- [x] `VS-1606` `P2` Exportable config bundle for migration/support.
  - Scope: export redacted app config, diagnostics context, and selected metadata for support/migration workflows.
  - Acceptance tests:
    - Integration: exported bundle opens and contains the expected redacted data set.
    - Security: no secrets/passwords/raw keys are included.
  - Done:
    - Settings > Advanced now includes `Export support bundle` action that creates a shareable zip under `Documents/VaultSync/Exports/Support`.
    - Bundle includes redacted config snapshot, local/destination metadata summaries, telemetry export zip, and recent diagnostics logs.
    - Sensitive values (passwords, key refs, credential usernames/domains) are redacted in the exported report payload.
- [x] `VS-1607` `P0` Optional sandbox restore mode with per-project default and per-run override.
  - Scope: sandbox workspace creation, review/apply path, cleanup options, and per-project restore-mode preference.
  - Done:
    - Project schema/model persists `restore_mode` (`direct` / `sandbox`) with migration-safe default `direct`.
    - Backups per-project cards expose restore-mode selection and persist it to project settings.
    - Restore runtime honors sandbox mode by restoring into an isolated preview folder while direct mode keeps existing behavior.
    - Restore confirmation supports per-run restore-mode override (`Direct` / `Sandbox`) before execution.
    - Sandbox completion provides post-restore actions (`Keep`, `Open sandbox`, `Apply to project`) with optional cleanup-after-apply.
    - Sandbox apply path includes pre-apply summary (files/overwrite/bytes) with explicit confirm/cancel gate.
  - Acceptance tests:
    - Integration: sandbox restore leaves destination untouched until confirm/apply.
    - UI: project default can be overridden at restore time.
    - Regression: direct restore remains available for users who do not want sandbox mode.
- [x] `VS-1608` `P1` Preset recommendations for detected project/library types.
  - Scope: suggest likely presets from observed folder structure/content when adding or editing a project.
  - Done:
    - Projects page computes high-signal preset recommendations from detected project markers (`Unity`, `Godot`, `Unreal`, `.NET`, `Node`, `Python`, `Rust`, `Avalonia`, `Blender`, `Video`) and caches results per project path.
    - Preset card surfaces recommendation reason text with one-click `Apply recommendation` action; manual preset selection remains unchanged.
    - Confidence gating was tightened for generic stacks (`Node`, `Python`, `.NET`) so recommendations are shown only when corroborating markers are present.
  - Acceptance tests:
    - UI: recommendations appear only when confidence is high enough to be useful.
    - Regression: manual preset selection always remains available.
- [x] `VS-1609` `P1` Project tagging + smart groups + bulk actions (pause/backup/snapshot by tag).
  - Scope: manual tags, computed smart groups, shared group filters, and bulk actions in Projects/Backups.
  - Current planning note:
    - Pulled forward from the original `1.7.x` bucket because organization now has direct product value for medium/large vaults.
  - Done:
    - Project schema/model now persists `tags` text on projects with migration-safe default empty value.
    - Projects page supports editable per-project tags (`comma-separated`) and persists updates to DB.
    - Projects list supports smart group filtering (`All`, `Work`, `Games`, `Media`, `Critical`, `Archive`) driven by tags plus high-signal preset/health hints.
    - Projects group controls now include bulk actions for `Snapshot group`, `Back up group`, and `Enable/Disable auto backups` for all registered projects in the active group.
- [x] `VS-1610` `P2` Per-destination retry policy with backoff + user status summary.
  - Scope: destination retry/backoff policy tuning and clearer retry status feedback for network/external targets.
  - Done:
    - Backup destination settings now persist per-destination retry policy (`attempts`, `base backoff seconds`) with bounded validation.
    - Manual and auto-backup flows now apply destination-scoped retry loops with exponential backoff and retry telemetry events.
    - Backups UI now surfaces retry status messaging and exhausted-retry summaries for failed destinations.
- [x] `VS-1611` `P1` Per-project verification policies (always/scheduled/manual).
  - Scope: verification mode per project, verification recency surfacing, and restore-confidence integration.
  - Done:
    - Projects schema/model now persists `verification_policy` with migration-safe default `always`.
    - Backups per-project cards now expose verification-policy controls (`Always`, `Scheduled`, `Manual`) and persist updates live.
    - Post-backup flow now evaluates project verification policy (`always` -> verify, `scheduled` -> auto runs, `manual` -> skip auto verify).
    - Metadata sync project settings now export/import verification policy alongside encryption settings.
  - Current planning note:
    - Pulled forward from the original `1.9.x` bucket because verification policy directly supports restore trust in `1.6`.
- [x] `VS-1612` `P1` Windowed backup-history card layout hardening.
  - Scope: prevent chip/pill overlap in narrow window widths and keep retention/status text readable without clipping.
  - Done:
    - Backups history item layout now reserves dedicated rows for retention and actions to avoid right-column collisions.
    - Size pill now uses adaptive width bounds instead of a fixed circular capsule in constrained layouts.
- [x] `VS-1613` `P1` Restore active-card stage and throughput parity.
  - Scope: ensure restore operations report a restore-specific stage and live transfer detail instead of backup-stage fallbacks.
  - Done:
    - Restore progress now emits processed/total bytes with live speed label in the active card.
    - Active backup stage detection now recognizes restoring/decrypting progress and shows restore-specific status text.
- [x] `VS-1614` `P2` Diff imported-type localization key parity.
  - Scope: add missing key used by diff preview/imported-type chips to prevent raw key rendering.
  - Done:
    - Added English localization key `Backups.Section.TypeImported` used by diff preview status chips.
- [x] `VS-1615` `P2` Quiet-hours window editor compact layout pass.
  - Scope: tighten quiet-hours input composition for windowed mode and remove excessive edge spacing.
  - Done:
    - Quiet-hours start/end fields now use centered compact groups with consistent widths and spacing.
- [x] `VS-1616` `P0` Patch updater trust-boundary hardening.
  - Scope: harden patch helper input/manifest handling (path normalization, traversal guards, install-root constraints, argument validation) for elevated update flows.
  - Done:
    - Patch apply requests are now normalized/validated before execution (absolute paths only, invalid PID guardrails).
    - Manifest file paths are resolved through strict `CombineUnderRoot` checks to block absolute/out-of-root traversal targets.
    - Patch staging and install copy now enforce root-bounded file resolution for both verify and write phases.
- [x] `VS-1617` `P1` Network-share delete fallback and user-guided recovery flow.
  - Scope: make backup delete on SMB/NAS robust when marker files are protected/locked, with deterministic prompt/retry/skip behavior.
  - Done:
    - Backup delete path now enforces destination-root-bounded path resolution before file-system operations.
    - Robust delete now performs a manual fallback pass after recursive delete failures and reports permission-denied outcomes explicitly.
    - Failure notifications now add a clear permissions/credentials remediation hint when protected files block delete.
- [x] `VS-1618` `P0` Updater request integrity binding and archive preflight validation.
  - Scope: bind elevated helper request file to launcher-provided integrity metadata and verify archive hash/size again before extraction.
  - Done:
    - Elevated helper now requires request-file SHA-256 passed by launcher and rejects tampered request payloads.
    - Request/manifest temp paths are constrained to trusted VaultSync temp roots in helper mode.
    - Archive hash/size are re-validated against manifest in helper before extraction.
- [x] `VS-1619` `P1` Backup path root-containment hardening for retention/restore/tray open flows.
  - Scope: enforce destination-root containment when resolving backup paths in retention cleanup, restore preparation, and tray open-folder flow.
  - Done:
    - Retention cleanup now rejects out-of-root backup paths before deletion attempts.
    - Restore preparation now rejects backup paths that resolve outside destination roots.
    - Tray backup-folder resolution/open flow now uses root-containment checks before filesystem access.
- [x] `VS-1620` `P2` Config read retry async cleanup.
  - Scope: remove blocking sleep from config read retry path to reduce UI-thread contention under lock races.
  - Done:
    - Config load retry now uses async backoff/read flow (`Task.Delay`, async stream/file reads) instead of blocking `Thread.Sleep` loops.
- [x] `VS-1621` `P1` Windowed viewport-width fill for primary pages.
  - Scope: ensure Backups/Dashboard/Settings root content stretches to available `ScrollViewer` viewport width while preserving readability max-width caps.
  - Done:
    - Backups, Dashboard, and Settings roots now bind `MinWidth` to the containing `ScrollViewer` viewport width.
    - Windowed layouts now use available horizontal space instead of collapsing to narrow centered columns.
- [x] `VS-1622` `P1` Backups windowed panel and list-height normalization.
  - Scope: remove hard vertical caps that caused uneven left/right panel heights and rebalance panel split for narrower windows.
  - Done:
    - Removed Backups per-project/history hard `MaxHeight` constraints that clipped panel utilization.
    - Backups main area now uses a `3*:2*` split and tighter control widths/wrap behavior for per-project card fields.
- [x] `VS-1623` `P2` Projects/Settings windowed control overflow cleanup.
  - Scope: reflow dense control rows in Projects details and Settings Advanced to prevent truncation/overflow in windowed mode.
  - Done:
    - Projects details control panel now uses a 2x2 responsive grid for destination/encryption/health blocks.
    - Settings Advanced action rows now wrap buttons and copy in narrow widths.
    - Near-zero storage delta now displays neutral `? ~0 B` for sub-1KB changes.
- [x] `VS-1624` `P2` Backups activity chart card empty-space collapse.
  - Scope: remove oversized empty space under the Backups 7-day bars in windowed mode.
  - Done:
    - Summary/activity row now uses explicit auto row sizing.
    - Activity card is top-aligned so it keeps chart content height instead of stretching to adjacent summary card height.

### 1.6.1 follow-up patch backlog
- [ ] `VS-1625` `P1` Restore runtime localization parity.
  - Scope: ensure restore-progress and active-card restore states always resolve through shipped localization keys, including runtime status text such as `Backups.Status.Restoring`.
  - Acceptance:
    - Active restore cards never show raw localization keys in the UI.
    - `strings.en.json` includes all restore-runtime keys required by current restore flows.
- [ ] `VS-1626` `P1` Restore-mode dropdown display binding hardening.
  - Scope: ensure restore confirmation dropdowns render `Direct` / `Sandbox` labels through explicit display templates/bindings instead of falling through to the view locator.
  - Acceptance:
    - Restore confirmation never shows `View not found for RestoreModeOption`.
    - Both restore-mode options render correctly in the dialog and any shared restore-mode selector surface.

## 1.7.x
### Release intent
- `1.7` should be the reliability/repair release:
  - make backup history, retention, restore readiness, and updater eligibility deterministic and explainable.
  - add guided remediation before adding new collaboration surface area.
- This means `1.7` should optimize for:
  1. deterministic data repair,
  2. retention safety,
  3. updater/serviceability diagnostics,
  4. operator-facing doctor workflows.

### Proposed delivery phases
1. Phase `A` (integrity backbone): `VS-1706` -> `VS-1711` -> `VS-1701` -> `VS-1705` -> `VS-1712`
   - Goal: know whether indexes are trustworthy before auto-repair or retention decisions run.
2. Phase `B` (guided repair UX): `VS-1702` -> `VS-1714` -> `VS-1717`
   - Goal: surface deterministic repair plans with dry-run/apply and conflict awareness.
3. Phase `C` (update/serviceability): `VS-1707` -> `VS-1708` -> `VS-1709` -> `VS-1718`
   - Goal: make update targeting, patch eligibility, and release gates auditable and support-friendly.
4. Phase `D` (capacity + maintenance): `VS-1703` -> `VS-1720` -> `VS-1716` -> `VS-1710` -> `VS-1715` -> `VS-1713`
   - Goal: give operators quota planning, checkpointed retry resilience, maintenance windows, startup diagnostics, and restore-readiness signals.
5. Phase `E` (dashboard refresh): `VS-1719` -> `VS-1721`
   - Goal: modernize the dashboard information hierarchy and visual clarity without losing VaultSync's current identity or familiar navigation, then carry shared project-tag color semantics consistently across the app.

### Revised planning notes
- `VS-1704` is moved out of `1.7` and into `1.8` as a deliberate scope cut.
  - Reason: shared-vault access control and audit trails are a separate product stream and would dilute the reliability/repair focus of `1.7`.
  - `1.7` release should not ship until:
    - orphan detection/remap is deterministic and idempotent,
    - retention can prove it preserves at least one restorable point per project,
    - updater diagnostics explain channel/target/patch eligibility without debug builds,
    - doctor workflows provide dry-run before mutation.
  - Beta builds for the `1.7` cycle should use prerelease app versions such as `1.7.0-beta.1`, while the final Stable cut remains `1.7.0`.

- [x] `VS-1701` `P0` Deterministic orphan-backup remap and repair engine. _(Done)_
  - Scope: remap only through trusted exact links (`backup.snapshot_id -> snapshots.project_id` and exact external-id matches), never name/path heuristics.
  - What it takes:
    - introduce a repair-evidence model (`exact snapshot link`, `exact external-id match`, `destination identity match`, `rejected heuristic`).
    - persist remap job results and reasons so re-runs are idempotent and diagnosable.
    - expose unresolved buckets (`missing snapshot`, `missing project`, `ambiguous match`, `identity mismatch`) for doctor/support surfaces.
  - Current status:
    - Done: deterministic dry-run repair plans derive exact backup->project remaps from authoritative snapshot ownership and report unresolved orphan buckets with stable codes.
    - Done: repair planning remains exact-link only and can be rerun idempotently without introducing name/path heuristics.
  - Depends on:
    - `VS-1706` startup consistency scan signals.
    - `VS-1712` stable destination identity model.
  - Acceptance:
    - Orphan remap jobs are deterministic and idempotent.
    - Diagnostics/support bundle include remapped/unresolved counts and reasons.
- [x] `VS-1702` `P0` Manual repair action for backup/project links. _(Done)_
  - Scope: add `Settings/Doctor` repair flow with dry-run and apply modes.
  - What it takes:
    - reusable repair-plan DTOs shared by UI, diagnostics export, and future CLI flows.
    - dry-run/apply orchestration with explicit counts, sample items, and post-apply summary.
    - mutation audit log entry for every repair apply action.
  - Depends on:
    - `VS-1701` deterministic repair engine.
  - Current status:
    - Done: Settings > Advanced exposes a manual backup-index repair panel with dry-run scan and exact-fix apply actions.
    - Done: repair runs report exact remap counts, blocked orphan buckets, and post-apply rescans without mutating valid mappings.
  - Acceptance:
    - UI shows what will be relinked before apply.
    - User can run safe repair without touching valid mappings.
- [x] `VS-1703` `P1` Destination quotas + cleanup suggestions. _(Done)_
  - Scope: per-destination quota targets, warning thresholds, and suggested cleanup candidates by age/size/protection status.
  - What it takes:
    - persist per-destination quota/threshold settings.
    - rank cleanup candidates from existing retention metadata without suggesting protected backups.
    - surface �space to recover� estimates and tie into health/readiness panels.
  - Current status:
    - Settings > Advanced now persists per-destination soft quota and warning-threshold values.
    - Backups destination cards now show stored bytes plus cleanup suggestions derived from unprotected backup candidates only.
  - Acceptance:
    - Quota warnings are visible before destination exhaustion.
    - Cleanup suggestions never include protected backups as auto-candidates.
- [x] `VS-1705` `P0` Retention delete resilience v2. _(Done)_
  - Scope: when oldest non-protected delete fails, continue to next eligible non-protected entry and report structured failure reasons.
  - What it takes:
    - refactor retention candidate evaluation into a reusable ordered plan.
    - preserve per-candidate failure reasons (`permission denied`, `out-of-root`, `unreachable`, `locked`, `verify failed`).
    - ensure delete attempts and skip decisions are visible in diagnostics and user-facing summaries.
  - Depends on:
    - `VS-1711` chain preflight.
  - Current status:
    - Done: retention now builds an ordered deletion plan that can skip the oldest candidate when deleting it would remove the last metadata-valid restore point.
    - Done: delete failures emit structured reason codes so diagnostics and later summaries can reuse stable failure classifications.
  - Acceptance:
    - Retention does not halt on first non-protected delete failure when other eligible entries exist.
    - Protected backups are always skipped.
- [x] `VS-1706` `P1` Startup backup-index consistency checks. _(Done)_
  - Scope: lightweight integrity scan for backup/snapshot/project links and destination-path consistency with non-blocking warnings.
  - What it takes:
    - add a cheap startup scan model with bounded work (sampled/full depending on vault size).
    - classify findings as `warning`, `repairable`, `critical`.
    - cache last scan summary so UI can show status without rescanning synchronously.
  - Current status:
    - In progress: first pass now runs as a deferred startup task after metadata auto-import and before update checks.
    - In progress: initial scan covers missing/duplicate external IDs plus snapshot/project/backup relationship mismatches, with runtime diagnostics summary state in `AppViewModel`.
    - In progress: findings now emit deterministic samples and persist a lightweight last-scan summary for support-bundle reuse.
  - Acceptance:
    - Startup scan surfaces actionable warnings without blocking app launch.
    - Scan output is available in diagnostics/support bundle.
- [x] `VS-1707` `P1` Updater channel and release-target diagnostics hardening. _(Done)_
  - Scope: expose candidate channel/branch resolution and release-target diagnostics to reduce mis-publish ambiguity.
  - What it takes:
    - persist update resolution trace (`channel`, `branch`, `tag`, `asset`, `why rejected`).
    - add operator-facing diagnostics surface and support-bundle export.
    - keep messages user-readable without exposing internal-only noise by default.
  - Current status:
    - In progress: update checks now persist channel/decision/candidate diagnostics alongside the selected release result.
    - In progress: Settings > Advanced now surfaces the persisted diagnostics summary next to the existing update status block.
    - In progress: support bundles now export the same redacted update diagnostics so release-target decisions can be inspected off-box.
  - Acceptance:
    - Support diagnostics clearly show selected candidate release and why.
    - Channel mismatch scenarios are visible to operators without debug builds.
  - [x] `VS-1708` `P1` Patch chain compatibility preflight. _(Done)_
    - Scope: explicit preflight validation for `current -> target` patch chain and required assets before showing patch install option.
    - What it takes:
      - explicit patch-chain model (`current`, `intermediate`, `target`, `supported`, `missing asset`, `requires installer`).
      - UI gate so patch CTA is shown only when eligibility is proven.
      - release tooling support so manifests expose enough chain metadata.
    - Depends on:
      - `VS-1707` updater target diagnostics.
    - Current status:
      - Done: patch checks now validate base version, target version, manifest availability, and manifest file entries before exposing patch install.
      - Done: preflight outcomes now persist stable status codes/messages alongside update diagnostics and export through support bundles.
      - Done: Settings > Advanced shows the current patch preflight outcome as part of the updater diagnostics summary.
      - Done: prerelease labels are compared explicitly so beta `1.7.0-*` builds do not collapse into stable `1.7.0` during patch matching.
      - Done: release asset workflow inputs are guarded so beta builds run from `Dev` with prerelease targets and stable builds run from `Stable` without prerelease targets.
    - Acceptance:
      - Patch button appears only when chain/assets are valid.
      - Installer fallback messaging states precise incompatibility reason.
      - Release asset workflow rejects beta/stable branch mismatches and invalid prerelease target formats before build work starts.
- [x] `VS-1709` `P2` Support bundle update/repair telemetry expansion. _(Done)_
  - Scope: include update candidate resolution trace, patch eligibility details, and orphan/repair summaries in redacted support exports.
  - What it takes:
    - extend support-bundle schema with stable redacted sections for updater/repair outcomes.
    - version the schema so support tooling can rely on field names across minor releases.
  - Current status:
    - In progress: support bundles now export persisted updater decision traces and patch-preflight eligibility results from advanced config.
    - In progress: doctor repair dry-run/apply flows now persist lightweight repair telemetry (actions, blocked buckets, codes, last apply state) for support exports.
    - In progress: metadata conflict tracking now persists conflict-resolution telemetry and exports pending conflict summaries for cross-machine triage.
  - Acceptance:
    - New telemetry sections are redacted and stable for support use.
- [x] `VS-1710` `P2` Scheduled maintenance window jobs. _(Done)_
  - Scope: optional scheduled health/repair/cleanup routines with summary notifications.
  - What it takes:
    - background scheduler model that reuses quiet-hours and retry policy concepts.
    - job history/logging so maintenance is explainable and non-silent.
    - opt-in defaults only; no surprise background mutation on upgrade.
  - Current status:
    - In progress: Settings > Advanced now exposes an opt-in maintenance window with per-task toggles for consistency scan, repair dry-run, and metadata refresh.
    - In progress: App startup/settings reload now wire a maintenance timer that runs only inside the configured window and records last-run status in advanced config.
  - Acceptance:
    - Maintenance jobs run only within configured windows and emit clear run summaries.
- [x] `VS-1711` `P0` Backup chain preflight before retention prune. _(Done)_
  - Scope: validate there is at least one restorable point per project before pruning non-protected backups.
  - What it takes:
    - define �restorable point� precisely across direct/sandbox/encrypted/imported histories.
    - integrate with retention planner before delete execution, not after.
    - emit clear block reasons when prune would violate restore safety.
  - Current status:
    - In progress: retention now simulates prune candidates and blocks when deletion would remove the last metadata-valid restore point for the project.
  - Depends on:
    - `VS-1706` consistency scan.
  - Acceptance:
    - Retention never leaves a project without a restorable backup chain unless explicitly user-confirmed.
    - Preflight result is logged and surfaced in diagnostics.
- [x] `VS-1712` `P1` Destination identity stability and remount continuity. _(Done)_
  - Scope: introduce stable destination identity checks across remove/re-add cycles to reduce index drift and false-orphan scenarios.
  - What it takes:
    - define a durable destination identity fingerprint beyond path/alias.
    - store identity in metadata/import/export so re-add and remount can be matched safely.
    - distinguish same-path-new-device from same-device-new-mount cases.
  - Current status:
    - Stable destination fingerprints now derive from canonical path + credential + mount mode.
    - Preferred-destination IDs in UI and metadata import now normalize legacy alias/path values onto stable destination identities.
  - Acceptance:
    - Re-adding the same destination path/identity preserves project routing and history linkage where exact identity matches.
    - Mismatch cases are reported with explicit remediation guidance.
- [x] `VS-1713` `P1` Restore-readiness scorecard in Backups and Dashboard. _(Done)_
  - Scope: add an at-a-glance restore-readiness status using last backup recency, verification recency, destination reachability, and unresolved integrity warnings.
  - What it takes:
    - compute a stable readiness model from existing health/verification/reachability signals.
    - make the score explainable with links to failing inputs rather than a black-box number.
    - avoid expensive recompute on every page refresh.
  - Depends on:
    - `VS-1706`, `VS-1711`, and `VS-1712`
  - Acceptance:
    - Scorecard is explainable and links to the underlying failing signals.
    - No blocking UI regressions on large project sets.
  - Current status:
    - Backups and Dashboard now compute a shared readiness summary with per-project labels, reasons, and aggregate ready/attention/risk/unavailable counts from backup recency, verification policy, destination reachability, and startup consistency results.
    - Readiness headline/detail/count labels are now formatted from localized UI copy instead of hard-coded English dashboard strings.
- [x] `VS-1714` `P1` Doctor workflows for guided remediation. _(Done)_
  - Scope: guided repair actions for common states (orphaned links, unreachable destination, stale verification, inconsistent metadata cache).
  - What it takes:
    - reusable doctor card/action model with dry-run/apply + remediation guidance.
    - action-specific validators for �can run now�, �needs destination online�, �needs user choice�.
    - support/diagnostic logging for every doctor action.
  - Depends on:
    - `VS-1702`
    - `VS-1706`
  - Current status:
    - Done: Settings > Advanced frames backup-index repair as a Doctor workflow with dry-run and Fix now actions plus guided remediation copy.
    - Done: every doctor repair scan/apply action writes structured diagnostics log entries for support-bundle auditability.
  - Acceptance:
    - Each doctor action has a dry-run summary and explicit apply step.
    - All mutations are audit-logged in diagnostics/support bundle.
- [x] `VS-1715` `P2` Non-blocking startup diagnostics timeline. _(Done)_
  - Scope: startup timeline with phase durations (config load, repo init, destination probe, metadata warm-up, update check) and slow-path attribution.
  - What it takes:
    - lightweight startup spans with bounded retention.
    - diagnostics-only collection path that does not become another startup tax.
  - Acceptance:
    - Timeline is available in diagnostics and support bundle.
    - Normal startup path remains non-blocking.
  - Current status:
    - In progress: startup now records stable constructor/deferred-startup phase checkpoints and persists the latest timeline summary in advanced config.
    - In progress: Settings diagnostics and support bundles now surface the last startup timeline with total duration and per-phase elapsed milliseconds.
- [x] `VS-1716` `P2` Retention simulation mode in settings. _(Done)_
    - Scope: preview retention outcomes per project/destination without deleting data.
    - What it takes:
      - reuse the same retention planner as real delete flow.
      - make simulation output diffable against current protected/kept/deleted buckets.
    - Current status:
      - Done: retention simulation reuses the retention preflight and delete planner without mutating data.
      - Done: Settings > Backups exposes a simulation preview with per-project reclaim/block summaries and protected backups highlighted as retained.
  - Acceptance:
    - Simulation output matches actual retention behavior on subsequent apply.
    - Protected backups are always highlighted as retained.
- [x] `VS-1717` `P1` Cross-machine metadata conflict resolver UX. _(Done)_
  - Scope: detect and resolve conflicting project-level metadata updates (destination/restore mode/tags/verification policy) with explicit conflict resolution options.
  - What it takes:
    - define conflict records with source machine/time/value deltas.
    - choose precedence rules that are explicit, not implicit overwrite.
    - provide batch-safe resolution actions for repeated conflicts.
  - Depends on:
    - `VS-1714`
  - Acceptance:
    - Conflicts are visible with source machine/time context.
    - Resolver prevents silent overwrite of newer authoritative metadata.
  - Current status:
    - Done: metadata import now records tracked field conflicts instead of silently overwriting local destination / restore mode / verification / tags.
    - Done: Settings > Advanced Doctor now exposes pending conflict cards with `Keep local` and `Accept imported` actions.
- [x] `VS-1718` `P2` Release readiness gate checklist automation. _(Done)_
  - Scope: scripted pre-release checks for patch assets, installer presence, changelog/whats-new parity, and project board release completeness.
  - What it takes:
    - one scripted gate command with machine-readable + human-readable output.
    - GitHub/project/release-asset queries wired into a deterministic checklist.
  - Current status:
    - In progress: added `scripts/release_readiness_gate.ps1` to validate UI/installer version parity, unreleased changelog alignment, What's New version alignment, release asset presence, and project-board completion for the target release slice.
    - In progress: release docs now point to the gate command as part of the release checklist.
    - In progress: gate now distinguishes `PrePublish` vs `PostPublish` verification so asset-generation steps are emitted as warnings before upload and hard-fail only during final release verification.
  - Acceptance:
    - One command emits pass/fail with actionable errors.
    - Gate output is attachable to release notes/support workflows.
- [x] `VS-1719` `P2` Dashboard information architecture and visual modernization pass. _(Done)_
  - Scope: revisit the Dashboard layout so it feels more modern and operationally useful while keeping VaultSync's dark visual identity, navigation model, and familiar core cards.
  - What it takes:
    - redesign KPI/card hierarchy so the most actionable signals land first (`backups`, `restore readiness`, `alerts`, `storage`, `recent activity`).
    - replace the current stretched/empty-space-prone sections with responsive card groups that scale cleanly in both maximized and windowed modes.
  - Current status:
    - Done: the dashboard redesign is in place with a stronger operations header, dedicated recent-activity rail, and responsive trend/storage groups that behave predictably in windowed layouts.
    - Done: header and lower information groups were refined to remove random duplication and make readiness, activity, trend, and storage read as one consistent hierarchy.
    - Done: summary cards use accent-strip hierarchy, restore-readiness review sits in its own section, and backup storage cards explain why capacity is currently at risk.
    - Done: the KPI row wraps into stable-width cards so fullscreen layouts avoid oversized dead space and narrower windows keep a predictable card rhythm.
 - [x] `VS-1721` `P2` App-wide tag color editor and chip styling. _(Done)_
  - Scope: add a complete app-wide tag-color system, edited primarily from Projects, and apply those colors consistently wherever project tags render.
  - What it takes:
    - persist per-tag background/foreground/border overrides in appearance settings.
    - render colored chips through one shared tag appearance helper across Projects, Backups, and Dashboard activity.
    - preserve configured colors through settings export/import flows.
    - Current status:
      - Done: shared tag appearance resolution supports configurable colors, and Projects hosts the primary visual editor with a ring picker, quick swatches, live preview, and app-wide save/reset flows.
      - Done: the leftover Settings reminder panel was removed so tag-color editing lives only where users can actually do the work.
      - Done: onboarding points new users at the Projects tag-color flow instead of a duplicate Settings surface.
      - Done: Projects, Backups, and Dashboard activity render the same configured chips app-wide.
      - Done: the Projects editor uses a wrap-friendly layout with stronger quick swatches so it still reads clearly in narrower windows.
      - Done: the quick tag palette uses a standard hard-coded color set so it behaves more like familiar color pickers.
      - Done: tag presets stay chip-friendly so the quick colors make sense for tags instead of mirroring generic theme slots.
      - Done: swatch borders are contrast-aware so light preset colors remain readable on dark surfaces.
  - Acceptance:
    - Tag colors can be added, edited, reset, and removed from Projects without confusing duplicate entry points or broken layout.
    - The same tag uses the same colors anywhere it appears in the app.
    - Support-bundle settings export/import preserves configured tag colors.
    - Quick swatches read as obvious colors instead of empty placeholders, and the editor keeps working across smaller window sizes.
    - Quick tag colors cover the common neutral and accent colors users expect from a picker preset row.
    - Tag presets stay useful for chip styling instead of drifting into unrelated theme-only colors.
- [x] `BUG-17001` `P1` Doctor workflow command-state thread affinity fix. _(Done)_
  - Scope: ensure detached Doctor scan/apply/conflict actions marshal command-state and bound status updates onto the UI thread.
  - Current status:
    - In progress: backup repair and metadata-conflict flows now dispatch busy-state, status, notification, and command refresh updates through Avalonia's UI thread.
  - Acceptance:
    - Doctor workflows no longer emit `Call from invalid thread` traces during dry-run/apply operations.

- [x] `BUG-17002` `P1` Restore corrupted bundled UI font assets. _(Done)_
  - Scope: replace invalid bundled `.ttf` placeholders with valid Noto Sans binaries so the shipped font pack is deterministic across machines.
  - Current status:
    - In progress: corrupted `NotoSans*` and `NotoSansArabic*` placeholder assets have been replaced with valid binaries so Avalonia stops ingesting HTML masquerading as font files.
  - Acceptance:
    - bundled font assets open as valid font binaries instead of text/HTML payloads.
    - UI text rendering no longer depends on unpredictable system fallback caused by broken embedded assets.

- [x] `BUG-17003` `P1` Projects page discovery fallback when filesystem scan is empty. _(Done)_
  - Scope: keep the Projects page populated from registered database projects when directory discovery returns no items or misses known projects.
  - Current status:
    - In progress: registered projects are now merged into the Projects page source list so tracked entries still render when folder discovery is unavailable or partial.
    - In progress: Projects now render explicit empty-state and no-selection placeholders instead of leaving the list/detail panes visually broken when scan results or selection state are empty.
  - Acceptance:
    - Projects page no longer appears blank just because discovery root scanning returned zero items.
    - Registered projects remain visible and selectable from stored metadata paths.

- [x] `BUG-17004` `P1` Preserve Projects root across startup config read/write races. _(Done)_
  - Scope: stop `Projects root` from clearing itself across restarts when config reads race startup writes or transiently deserialize invalid/partial JSON.
  - Current status:
    - In progress: config writes now use temp-file replace semantics with a backup file, and config loads fall back to backup/last-known-good snapshots before defaulting to empty values.
    - In progress: safeguard is being verified against unreachable destination/startup stress scenarios so unrelated config saves cannot persist a blank projects root.
  - Acceptance:
    - `Projects root` persists across restart even if startup writes happen while the destination is unreachable or config reads are transiently busy.
    - transient config read failures no longer downgrade the in-memory config to defaults and then overwrite the saved root path.

- [x] `BUG-17005` `P2` Simplify custom theme editor hierarchy in Settings > Appearance. _(Done)_
  - Scope: make the custom theme editor easier to understand visually by reducing duplicate emphasis, clarifying the edit order, and tightening the picker/preview layout.
  - Current status:
    - Done: the editor follows a clearer preset -> target -> pick -> preview flow with a cleaner spectrum-focused picker.
    - Done: the theme editor uses wrap-based cards so the palette, picker, preview, and tuning controls scale better in windowed layouts.
    - Done: theme swatches render from explicit brush-backed palette entries, and quick colors stay usable for the selected slot.
  - Acceptance:
    - The theme editor reads as one coherent workflow instead of a pile of equal-weight controls.
    - Presets, target selection, picker, and preview are visually distinct and easier to scan in windowed mode.
    - Quick swatches render as obvious colors instead of neutral placeholders, and the picker no longer wastes space on low-value chrome.
    - Theme editing remains usable on narrower windows without the preview or tuning panels collapsing awkwardly.
    - Theme quick colors adapt to the selected slot instead of serving one generic palette for everything.

- [x] `VS-1722` `P2` Expand custom theme presets and advanced controls. _(Done)_
  - Scope: add more useful starter themes and an optional advanced editing mode without cluttering the default theme workflow.
    - Current status:
      - Done: the custom theme presets include OLED Black and Deep Blue variants for darker display preferences.
      - Done: the theme editor moves advanced sliders into the right-side panel and uses a more recognizable editor-style default palette.
      - Done: theme swatches are tightened into stronger preset tiles so neutral and light colors read clearly at a glance.
      - Done: palette clicks follow the active theme slot explicitly so users can see which section they are editing.
      - Done: theme quick colors are visually aligned with the tag-color swatches so both editors feel like one system.
      - Done: the theme default palette is unified with the tag picker presets instead of keeping a separate base row.
      - Done: the custom-theme palette block matches the Projects tag-picker layout instead of using a near-duplicate variant.
      - Done: the theme swatches no longer use a separate selected-state border and now render like the Projects tag swatches.
      - Done: the theme editor uses the same full quick palette used by the Projects tag editor instead of slot-filtered swatches.
      - Done: the theme section selector keeps one visible target active for palette clicks.
      - Done: the theme section chips use an explicit slot-selection path instead of loose toggles.
      - Done: theme palette clicks use an explicit immediate-apply path so the selected section updates instantly.
  - Acceptance:
    - Starter themes include clearly differentiated OLED black and dark blue options.
    - The default palette is understandable at a glance as the app's core colors and accents.
    - Advanced sliders are available in the spare right-side space without overwhelming the default theme editing flow.
    - Quick theme colors cover common editor-style neutrals and accents instead of looking sparse or placeholder-like.
    - Theme quick colors use familiar hard-coded presets instead of abstract placeholder swatches.
    - Theme quick colors stay usable for the selected slot category instead of requiring manual guesswork.
    - The active theme slot is obvious, and swatch clicks visibly apply to that slot instead of feeling ambiguous.
    - Theme and tag quick palettes share the same swatch language instead of feeling like different controls.
    - The default theme palette row matches the tag picker presets instead of diverging into a separate neutral set.
    - The custom-theme palette block matches the Projects tag-picker layout instead of remaining a lookalike clone.
    - Theme and tag swatches render the same borders instead of keeping subtly different selection chrome.
    - Theme quick colors use the same full colorful palette as the Projects tag editor instead of a filtered subset.
    - Theme quick colors always apply to the visibly selected theme section instead of an ambiguous stale target.
    - Theme section selection is explicit before palette colors are applied.
    - Theme quick colors update the selected section immediately instead of appearing to do nothing.


- [x] `BUG-17006` `P2` Polish dashboard readability, restore-readiness review flow, and shared shell controls. _(Done)_
  - Scope:
    - make restore-readiness risk states easy to inspect from Dashboard and Backups without hunting for the affected projects;
    - remove corrupted English separator glyphs and tighten summary copy in Dashboard, Backups, Projects, and retention surfaces;
    - improve collapsed sidebar presence, shared toggle/checkbox styling, Backups summary spacing, and the Projects tag-color editor layout.
  - Current status:
    - Done: Dashboard and Backups restore-readiness cards expose an in-place issue list with direct navigation back to Backups for action.
    - Done: corrupted English glyphs now use ASCII-safe fallbacks so mojibake does not leak into release builds.
    - Done: the shared shell/sidebar, toggle styling, Backups spacing, and Projects tag-color editor polish landed across the 1.7 UI passes.
  - Acceptance:
    - Restore-readiness summaries can be expanded in place to show who is affected and why.
    - English summaries no longer render broken separator glyphs anywhere in the updated surfaces.
    - Shared controls and collapsed navigation feel intentional instead of default or placeholder-like.

- [x] `BUG-17008` `P2` Gate noisy dev logging behind verbose mode. _(Done)_
  - Scope:
    - reduce backup, restore, destination, and dashboard runtime chatter that was useful during development and performance testing;
    - keep real failures visible while moving flow/progress tracing behind the explicit verbose logging path.
  - Current status:
    - Done: the shared runtime log gate now follows config instead of enabling verbose trace output automatically in debug builds.
    - Done: developers can still force full trace output with `VAULTSYNC_FORCE_VERBOSE=1` when they need raw flow logs without flipping app settings.
  - Acceptance:
    - normal beta and release runs do not emit backup-progress and restore-path chatter by default.
    - developers can explicitly re-enable the developer-oriented tracing when needed.
    - enabling verbose logging restores the gated runtime chatter for troubleshooting.

- [x] `BUG-17009` `P1` Batch diagnostics session-log writes behind one background writer. _(Done)_
  - Scope:
    - replace per-line `Task.Run` session-log writes in `DiagnosticsLogger` with a queued single-writer flush path;
    - keep heartbeat and crash logging behavior unchanged while reducing log-path thread-pool churn.
  - Current status:
    - Done: session-log writes are now queued and flushed in batches through one background writer loop.
  - Acceptance:
    - verbose/debug sessions no longer spawn one background task per log line.
    - session logs still capture lifecycle, crash, doctor, maintenance, and updater events reliably.
    - diagnostics logging no longer becomes a performance bottleneck under noisy runs.

- [x] `BUG-17010` `P1` Remove synchronous config reads from Projects group auto-backup command-state checks. _(Done)_
  - Scope:
    - stop calling `AppConfigStore.Load()` during Projects page `CanExecute` evaluation for group auto-backup actions;
    - use refreshed cached preference state instead so group command enablement stays responsive.
  - Current status:
    - Done: Projects group auto-backup command-state now uses a cached disabled-project id set refreshed from config on load/refresh and after updates.
  - Acceptance:
    - Projects group action enablement stays responsive even when config I/O is contended.
    - enable/disable group actions still reflect the latest saved preference state.
    - refresh/update flows keep command-state synchronized without UI-thread file I/O.

- [x] `BUG-17011` `P2` Left-align the Dashboard KPI card strip. _(Done)_
  - Scope:
    - keep the top Dashboard KPI cards anchored to the left edge instead of centering them within wide windows.
  - Current status:
    - Done: the Dashboard KPI `WrapPanel` now left-aligns its card row, so the cards stay anchored to the content edge instead of floating in the middle.
  - Acceptance:
    - Dashboard KPI cards align to the left in wide layouts.
    - existing wrap behavior is preserved on narrower windows.

- [x] `BUG-17012` `P1` Preserve Projects-managed tag colors when saving custom themes. _(Done)_
  - Scope:
    - stop Settings > Appearance from overwriting app-wide tag-color mappings while saving theme changes now that tag-color editing lives in Projects.
  - Current status:
    - Done: Settings no longer rewrites `Appearance.TagColors` during theme saves, so custom theme changes preserve the latest tag colors already stored in config.
  - Acceptance:
    - Saving a custom theme does not reset or replace existing tag colors.
    - Projects remains the authoritative editor for app-wide tag colors.
    - tag chips keep their saved colors after theme changes and app restart.

- [x] `VS-1720` `P1` Checkpointed retry support for interrupted backup transfers. _(Done)_
  - Scope: allow large backup uploads to resume from the last completed checkpoint instead of restarting the full transfer after a transient failure.
  - Why it matters:
    - reduces wasted time and bandwidth on large backups and unstable network destinations.
    - directly addresses user feedback about retry behavior on partial transfer failures.
  - What it takes:
    - define a durable checkpoint model for archive and non-archive transfer modes so partially uploaded data can be resumed safely.
    - guarantee consistency of partial uploads (`checkpoint metadata`, `finalization marker`, `validation before resume`, `safe discard path`).
    - integrate checkpoint awareness with retry/backoff logic, destination capability checks, and cleanup of abandoned partial payloads.
    - surface resumable vs restart-required state in diagnostics so failures remain explainable.
  - Dependencies:
    - `VS-1712` destination identity stability so resumed transfers bind to the correct target.
    - existing retry/backoff and upload-buffer logic in backup runtime.
  - Acceptance:
    - interrupted transfers resume from the last committed checkpoint when the destination supports it.
    - unsafe or stale partial payloads are rejected and restarted cleanly instead of producing corrupted backups.
    - diagnostics/support bundle clearly report checkpoint creation, resume, discard, and fallback-to-full-retry reasons.
  - Current status:
    - Done: archive uploads persist checkpoint metadata and preserve resumable incomplete backup folders per destination.
    - Done: prefix validation restarts archive uploads cleanly if partial payload bytes no longer match the rebuilt local archive.
    - Done: Settings diagnostics and support bundles surface the last checkpoint resume/discard/preserve outcome with byte progress and explanatory detail.
    - Done: non-archive backup paths continue through native rsync/robocopy runners, which already use restartable transfer semantics instead of restarting the whole backup set on the next run.

- [x] `VS-1724` `P1` Single-manifest multi-base patch compatibility. _(Done)_
  - Scope: allow one patch manifest to declare multiple exact compatible base versions so one patch release can safely serve more than one prior build.
  - What it takes:
    - extend patch manifest schema with an explicit base-version allowlist while keeping legacy single-base manifests valid.
    - enforce the same exact-match allowlist rules in both patch preflight and helper/apply paths.
    - surface allowed/matched base versions in updater diagnostics and support bundles.
    - update patch build tooling so release automation can emit one manifest with multiple explicit prior versions.
  - Safety constraints:
    - exact allowlist matching only; no version ranges or fuzzy compatibility.
    - malformed or inconsistent manifests fail closed.
    - helper/apply validation remains authoritative even if preflight previously succeeded.
  - Acceptance:
    - legacy manifests with only `previousVersion` still work unchanged.
    - multi-base manifests accept only explicitly listed base versions.
    - helper/apply rejects non-listed current versions before copying files.
    - diagnostics clearly explain allowed bases, matched base, and mismatch reasons.
  - Current status:
    - Done: manifest schema, preflight validation, helper enforcement, diagnostics, and patch builder all support a strict exact-base allowlist while preserving legacy single-base manifests.
    - Done: release workflow inputs now author multi-base manifests explicitly, and tests cover legacy manifests, exact allowlist matches, malformed allowlist rejection, and non-listed base rejection.

## 1.8.x
- [ ] `VS-1723` `P1` Refactor SettingsViewModel into feature partials.
  - Scope: split large settings responsibilities into feature-focused partial files, starting with the custom theme editor, to reduce change risk before the macOS work.
  - Current status:
    - Deferred from `1.7.x`: this remains a behavior-preserving refactor, not a release-critical shipping item.
    - The partial split can continue after `1.7` stabilization without blocking the release.
  - Acceptance:
    - Theme-editor logic no longer lives in the monolithic `SettingsViewModel.cs` file.
    - Existing Settings bindings continue to work without regressions.
    - The split makes later platform-specific settings work lower-risk and easier to review.

- [ ] `VS-1801` `P1` Multi-destination health scoring and auto-failover.
- [ ] `VS-1802` `P1` Cloud targets (S3-compatible, Backblaze, etc.) with encryption.
- [ ] `VS-1803` `P2` Automation hooks (webhooks/scripts on backup/restore events).
- [ ] `VS-1804` `P2` CLI parity with all major UI features.
- [ ] `VS-1704` `P2` Team workflows (shared vaults, access control, audit trails).
  - Scope: shared-vault collaboration primitives and operator audit visibility.
  - Planning note:
    - Moved from `1.7.x` so `1.7` can stay focused on reliability, repair, and updater determinism.
  - Acceptance:
    - Shared workflows stay optional and do not regress solo mode defaults.

## 1.9.x
- [ ] `VS-1902` `P1` App signing for trusted distribution.
- [ ] `VS-1903` `P2` Background integrity audits with alerts.

