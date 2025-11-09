# VaultSync CLI

Snapshot, sync, and verify project folders with rsync-backed mirroring.

## Commands
- `vaultsync init`
- `vaultsync add-project <name> <path> --preset <unity|dotnet|custom>`
- `vaultsync snapshot <name>`
- `vaultsync sync <name> <destination> [--dry-run]`
- `vaultsync verify <name> <destination> [--full|--percent 10]`
- `vaultsync list-projects [--json]`
- `vaultsync doctor [--check-dest <PATH>]`
- `vaultsync version`
