# VaultSync CLI — current supported command line

This branch retains the 1.8.9 flat command tree with the 1.9.0 planning identity.
Run `vaultsync --help` and `vaultsync COMMAND --help` for executable options.
Common commands: `list-projects`, `add-project`, `snapshot`, `history`, `diff`,
`sync`, `verify`, `restore`, `prune`, `watch`, `doctor`, `destinations`.
Mirroring uses live source bytes; restore uses recorded backups and verifies
stored bytes before changing the target. Unattended removal requires `--yes`.

The resource commands, versioned result envelopes, bundled task guides and
full automation rework belong to the [1.9.5 development branch](https://github.com/ATAC-Helicopter/VaultSync/tree/release/1.9.5).
Discovery, handbook and completion are developed against this flat tree in 1.9.1.
Earlier CLI audit/output documents retained in this tree describe that future
implementation; they do not describe the active 1.9.0 executable.

Legacy watcher safety is retained: finite read-only text dry runs, explicit `--db`,
shutdown cancellation/drain and explicit failure. Resource/JSON commands stay in 1.9.5.
