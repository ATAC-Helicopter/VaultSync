# Roadmap


## Near term 
- Stabilize updater: fix relaunch fail cases, clearer status, UI redesign.
- Incremental backups UX: clearer retention behavior, restore guidance, size reporting.
- Faster snapshot scanning on large projects (skip unchanged folders, cache heuristics).
- Richer restore flows (selective restore, dry-run previews, conflict prompts).
- Per-project destination selection when multiple destinations are configured.
- Backup encryption and password-protected backups.

## Mid term 
- Smarter storage usage reporting (per-project deltas, last-change summaries).
- Custom preset editor for filters and ignore rules.
- Team workflows: shared vaults, access control, audit trails.
- Cross-machine migration: import VaultSync projects and restore full history (sizes, snapshots, backups).
- App signing for trusted distribution.

## Long term 
- Multi-destination health scoring and auto-failover.
- Cloud backup targets (S3-compatible, Backblaze, etc.) with encryption options.
- Advanced automation hooks (webhooks, scripts on backup/restore events).
- CLI parity with UI features.
- macOS support.
- Exportable config bundle for easy migration and support.

