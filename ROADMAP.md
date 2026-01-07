# Roadmap


## completed 
- [x] Stabilize updater: relaunch fixes, clearer status, UI redesign, and patch compatibility guardrails.
- [x] Documentation refresh: expanded wiki with setup, usage, and troubleshooting guidance.

## Near term
- [ ] Cross-machine migration: import VaultSync projects and restore full history (sizes, snapshots, backups).
  - Integration plan:
    - Source of truth: backup destination stores portable metadata; local DB mirrors it.
    - Backup metadata: write/refresh `.vaultsync/meta` on each backup (history, snapshots, totals).
    - Sync: auto-merge metadata across machines; conflict resolution via timestamps + machine IDs.
    - Opt-out: per-destination toggle to disable metadata sync.
    - Encryption support: store encryption flags + key derivation params (never secrets).
    - Password flow: prompt on restore; cache only in-memory for session.
    - Non-zip backups: store per-backup metadata file next to data folder with encryption marker.
    - Tests: missing metadata, stale metadata refresh, merge conflicts, partial backup sets.
- [ ] Per-project destination selection when multiple destinations are configured.
  - Integration plan:
    - Data: `PreferredDestinationId` (nullable) to Project; migration default null.
    - UI: project settings -> destination dropdown (All/Auto/Specific); show in project card.
    - Backup flow: resolve destination list per project; if set, route only to that destination (fallback to active if missing).
    - Status: show destination label in active backup card + console lines.
    - Validation: warn if selected destination is inactive/unreachable; allow override.
- [ ] Backup encryption and password-protected backups.
  - Integration plan:
    - Data: add encryption settings (per-project + global defaults), store in config; no plaintext keys.
    - UX: encryption toggle with password + confirm; strength hint; require password on restore.
    - Crypto: AES-256 with per-backup salt + IV; derive key via PBKDF2/Argon2.
    - Backup flow: write encrypted container per backup under a vault folder (e.g., `.vaultsync/vault/`).
    - Vault rules: contents are not browsable/restorable without password; always stored as encrypted container.
    - Metadata: store non-secret metadata + key derivation params alongside the container.
    - Restore: prompt for password; validate; decrypt to temp; verify hash.
    - Migration: encrypted backups restore by password; export/import includes encryption metadata (never secrets).
    - Migration: existing backups remain unencrypted; show mixed-state badge.
    - Tests: encryption round-trip, wrong password, performance guardrails.
- [ ] Faster snapshot scanning on large projects (skip unchanged folders, cache heuristics).
  - Integration plan:
    - Cache: store per-project scan cache (path, mtime, size, file count).
    - Heuristics: skip folders with unchanged mtime/size; fall back on deep scan when uncertain.
    - Safety: periodic full scan (e.g., every N runs) to avoid drift.
    - Telemetry: record scan time and skipped counts; surface in logs.
    - Settings: optional "Aggressive scan cache" toggle for power users.
    - Tests: cache hit/miss, rename/move detection, safety full scan cadence.
- [ ] Team workflows: shared vaults, access control, audit trails.
  - Plan: define shared-vault model; add audit log schema; role-based access controls.
- [ ] Dry-run backups (estimate size/time before starting).
  - Plan: preflight scan to estimate bytes/files; show ETA + destination fit; allow cancel.
- [ ] Backup bandwidth limits and quiet hours (avoid congesting networks).
  - Plan: schedule window + throttling settings; apply to network copy workers; show active policy in status.
- [ ] Richer restore flows (selective restore, dry-run previews, conflict prompts).
  - Plan: restore wizard; preview file list + size; conflict resolution prompts.
- [ ] Incremental backups UX: clearer retention behavior, restore guidance, size reporting.
  - Plan: explain retention rules in UI; show "last full vs delta" info; add restore tips.
- [ ] Snapshot diff summaries (top changed folders/files, size deltas).
  - Plan: compute delta summary after backup; show top changes; exportable summary.

## Mid term
- [ ] Smarter storage usage reporting (per-project deltas, last-change summaries).
- [ ] Custom preset editor for filters and ignore rules.
- [ ] Backup health timeline (success rate, last failure reason, trend chart).
- [ ] Project tagging and bulk actions (pause, backup, snapshot by tag).
- [ ] Per-destination retry policy with backoff and user-facing status summary.
- [ ] Exportable config bundle for easy migration and support.
- [ ] Destination quotas and cleanup suggestions (per-target caps).
- [ ] Restore point browser with compare and timeline view.

## Long term
- [ ] Multi-destination health scoring and auto-failover.
- [ ] Cloud backup targets (S3-compatible, Backblaze, etc.) with encryption options.
- [ ] Advanced automation hooks (webhooks, scripts on backup/restore events).
- [ ] CLI parity with UI features.
- [ ] macOS support.
- [ ] Per-project verification toggle (always verify, verify on schedule, or manual).
- [ ] App signing for trusted distribution.
- [ ] Background integrity audits with alerts.
