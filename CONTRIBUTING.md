# Contributing

Thanks for helping improve VaultSync.

## Before you start
- Check the roadmap and open issues to avoid duplicating work.
- For large changes, open a discussion first.

## Development setup
1) Install .NET 8 SDK.
2) Clone the repo and restore:
   `dotnet restore`
3) Run the UI:
   `dotnet run -f net8.0-windows10.0.19041.0 --project src/VaultSync.UI/VaultSync.UI.csproj`

## Pull requests
- Keep changes focused and small when possible.
- Update `CHANGELOG.md` for user-facing changes.
- Add or update docs in `docs/wiki/` when behavior changes.

## Coding standards
- Prefer clear, readable code over clever tricks.
- Avoid unnecessary allocations in hot paths.
- Keep UI work off the UI thread when possible.

## Reporting bugs
See `docs/wiki/Reporting-Bugs.md` for the preferred format.
