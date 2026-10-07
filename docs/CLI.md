# VaultSync CLI handbook — 1.9.1 development

This branch adds discovery, bundled documentation and completion to the
supported flat CLI. It does not contain the future grouped/resource command tree.
Run `vaultsync`, `vaultsync --help`, `vaultsync docs --full`,
`vaultsync docs --task inspect` or `vaultsync completion bash|zsh|powershell`.
Build/package the CLI, add the tool directory to PATH and verify the executable
version. `dotnet tool install --global vaultsync.cli --version 1.9.1` is a future
published-package example; this development version has not been released.

## Existing workflows

Register with `add-project NAME PATH --db PATH`; inspect with `list-projects`
(`--json` preserves legacy output), `history NAME` and `diff NAME`.
`snapshot NAME` indexes/hashes the source; it does not create stored recovery bytes.
`sync NAME DEST --dry-run` previews mirroring from the live source.
`restore NAME TARGET --dry-run` restores recorded folder backups and verifies
stored bytes before changes. `prune --dry-run` previews bounded retention scope.
`watch NAME --db PATH --dry-run` prints a finite read-only plan.
Unattended removal requires `remove-project NAME --yes`; quiet is not authorization.
Use an isolated database and disposable paths when testing development builds.

## Compatibility and migration

Existing flat command names and legacy JSON stay supported. Grouped commands,
backup creation/inspection/verification, selective restore and v1 result envelopes
are preserved in release/1.9.5 and are not available in this branch.
Archive/encrypted CLI parity, database-independent recovery and disk/offsite
support remain unqualified. Shell completion does not validate path safety.

## Shell setup

Bash: `vaultsync completion bash > vaultsync.bash`; source the generated file.
Zsh: put `vaultsync completion zsh` in `_vaultsync` on `fpath`, then run `compinit`.
PowerShell: load `vaultsync completion powershell` from the profile.
Generated scripts come from executable help. Bash is checked locally; Zsh and
PowerShell runtime qualification still requires their supported environments.
