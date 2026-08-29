# Local storage and cleanup

VaultSync separates durable user state from disposable working data. Automatic
cleanup is intentionally allowed to remove only data that the application can
recreate. It never treats a backup destination, database, configuration file,
credential store, or managed mount contents as cache.

## Storage map

| Location | Contents | Policy |
| --- | --- | --- |
| User configuration directory (`~/.vaultsync` by default) | Settings, optional legacy database/state, telemetry identity, and legacy command logs | Settings and identity are durable. Logs older than 14 days are removed and retained logs are capped at 10 MB. Abandoned configuration writes older than one hour are removed. |
| Per-user application-data `VaultSync` directory | Main database, credentials, installation identity, UI preferences, diagnostics, caches, updater working data, and macOS managed mount points | Durable state is retained. Disposable subdirectories follow the limits below. Exact crash-abandoned identity and credential-index writes are removed after one hour. The `mounts` subtree is never automatically deleted. |
| `diagnostics` | Session logs, samples, traces, and optional hang dumps | Pruned at startup and every six hours. Five recent text artifacts are retained per type, at most one hang dump is retained, and combined diagnostic evidence is capped at 128 MB. |
| `cache/scan` | Re-creatable directory scan acceleration | Files older than 30 days are removed; retained entries are capped at 20 MB. |
| `cache/release-assets` | Digest-verified release manifests | Files older than 180 days are removed; retained entries are capped at 10 MB. Crash-abandoned temporary writes with the exact verified-cache name are removed after one day. |
| `patches` | Downloaded patch archives | Verified archives older than one day are removed. Incomplete downloads are already removed when a download finishes or fails. |
| `patch-runtime` | Temporary copied updater helper and extraction directories | Helper copies older than one day and staging directories older than one hour are removed. The helper log is limited to 1 MB and 14 days. |
| `exports/support-*` | Private sanitized support-bundle staging | Exact staging directories abandoned for more than one day are removed. Completed ZIP exports in Documents remain user-owned and are never removed automatically. |
| OS temporary directory | Decrypted-open workspaces, restores, metadata-import recovery snapshots, key rotation, archive uploads, updater downloads, recovery tests, and tool exclude files | Each operation cleans its own working data. Startup also removes abandoned VaultSync working data older than one day. Decrypted-open workspaces have a shorter in-app lock timeout. |
| OS temporary `vaultsync-meta-export` directory | Active deferred portable-metadata queues and recently consumed replay evidence | Active queues are durable until replay succeeds. Consumed queues remain recoverable for one day, then are removed by link-safe cleanup. |
| OS temporary `vaultsync-telemetry-export` directory | Manually generated telemetry ZIPs | Exact telemetry exports are retained for up to 30 days and capped at 100 MB newest-first. Unrelated files are never selected. |
| User-selected backup destinations | Backup payloads and portable metadata | Governed only by configured backup retention and protection rules; never by cache cleanup. |

## Managed network mounts on macOS

`~/Library/Application Support/VaultSync/mounts` contains mount points, not a
fallback backup destination. Before creating a backup folder, VaultSync now
requires a managed path to be backed by a currently mounted SMB or NFS
filesystem. A directory that merely still exists after a share disconnects is
rejected. This prevents remote backup bytes from silently consuming the local
system drive.

Existing files found beneath an unmounted managed path are not deleted
automatically: they may be the only copy of a backup produced during a previous
disconnect. The user should inspect and move or remove them only after verifying
that the intended remote destination contains the same restore point.

## Design rules

- Cleanup runs best-effort and can never block application startup.
- Recent or active working files remain available long enough for retries.
- Cleanup follows exact VaultSync-owned names beneath resolved per-user roots.
- Symbolic-link files and directories are removed only as links and are never
  traversed while sizing or deleting disposable content, including when they
  appear below an otherwise ordinary VaultSync working directory.
- Size caps discard the oldest disposable files first.
- The confirmed **Clear local cache** action removes the same cache, patch,
  legacy-log, crash-report, and temporary-data families immediately, while
  continuing to exclude databases, configuration, credentials, backups, and
  managed mount contents.
