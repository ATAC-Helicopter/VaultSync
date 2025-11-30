# Patch-based Updater

VaultSync now tracks the `stable` branch via GitHub Releases, but the UI no longer forces a full installer download on every push. Instead, we deliver _delta patches_ that touch only the binaries and assets that changed between releases, keep the user data/config untouched, and hand off the final apply step to a small updater helper.

## Release pipeline

1. Tag the release (for example, `v0.4.2`) once you are ready to ship.
2. Run `git diff --name-only <previous-tag> <new-tag>` to discover which outputs changed. Use the same build artifacts that you ship for the installer (the Avalonia output, core DLLs, the CLI tool, etc.).
3. Copy the changed files into a staging folder and create a manifest (`vaultsync-patch-<platform>.json`) describing the patch.
4. Zip the staged files into `vaultsync-patch-<platform>.zip`. Publish both the manifest and the ZIP as release assets on GitHub. The manifest and the archive share the platform suffix (`windows`, `macos`, `linux`) so the app knows which asset to use.

   - **Windows installer**: we build the installable `.exe` via Inno Setup using `installer/VaultSyncInstaller.iss`. This script already points at the Windows publish output and embeds the app icon; run it with the Inno Setup compiler to produce `VaultSync-Setup-<version>.exe` before tagging the release.
   - **macOS/Linux patching**: package the patch asset manually (e.g., zipped `dotnet publish` output) and include the same `vaultsync-patch-macos.json`/`.zip` naming so the updater matches the release with the platform-specific delta.

## Manifest structure

The manifest is simple JSON. Example:

```json
{
  "previousVersion": "0.4.1",
  "targetVersion": "0.4.2",
  "archiveSize": 1234567,
  "archiveSha256": "abcdef1234...",
  "files": [
    {
      "path": "VaultSync.UI.dll",
      "sha256": "111...",
      "size": 204800
    },
    {
      "path": "VaultSync.Core.dll",
      "sha256": "222...",
      "size": 102400
    }
  ]
}
```

Each entry lists the relative path (from the install directory), its SHA-256 checksum, and the expected size. The updater reads this manifest, verifies that the current version matches `previousVersion`, and only then downloads the zipped delta.

## Runtime updater responsibilities

VaultSync’s UI downloads the manifest, validates it against the running `AssemblyInformationalVersion`, and stages the ZIP file under `%AppData%/VaultSync/patches`. The patch is verified (size + checksum) before notifying the user that it is ready.

The actual replacement of files is performed by the updater helper: once the delta is staged, launch the helper (a small console/WinUI utility shipped alongside the main app) with the patch path and manifest. The helper:

- Stops VaultSync if it is running.
- Copies the patched files into the install folder using atomic copy/rename semantics so a failure can roll back.
- Preserves config/DB located under `~/.vaultsync` / `%APPDATA%/VaultSync`.
- Restarts VaultSync (or leaves it stopped) and reports success/failure.

If the delta cannot be applied (wrong `previousVersion`, checksum mismatch, or the helper fails to complete), fall back to the full installer asset.

## UI flow

- The header warns when a new patch is available and shows release notes in the tooltip.
- The “Install patch” button downloads the delta and stages it locally (the release page button remains for inspection).
- A notification and a small status line tell the user where the patch lives and prompt them to run the updater helper.

This keeps the existing per-push update model but switches downloads to a safe, file-level patch process that never overwrites the user’s database or settings.
