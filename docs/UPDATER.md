# Patch-based Updater

VaultSync tracks the `stable` branch via GitHub Releases, but the UI no longer forces a full installer download on every push. Instead, we deliver _delta patches_ that touch only the binaries and assets that changed between releases, keep the user data/config untouched, and hand off the apply step to a built-in patch helper.

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

VaultSync's UI downloads the manifest, validates it against the running `AssemblyInformationalVersion`, and stages the ZIP file under `%AppData%/VaultSync/patches`. The patch is verified (size + checksum) before invoking the built-in helper to apply it.

The helper is the same executable launched with:

```
VaultSync.UI.exe --apply-patch <zipPath> <manifestPath> <installDir> [--restart] [--waitpid=<pid>]
```

- UI calls the helper, then shuts down so files can be replaced.
- Helper waits for the UI process to exit, extracts the ZIP to a temp folder, verifies each file (size + SHA-256), then copies it into the install directory.
- Config/DB under `~/.vaultsync` / `%APPDATA%/VaultSync` are untouched.
- If `--restart` is passed (default from the UI), the app relaunches after a successful apply.

If the delta cannot be applied (wrong `previousVersion`, checksum mismatch, or the helper fails to complete), fall back to the full installer asset.

## UI flow

- The header warns when a new patch is available and shows release notes in the tooltip.
- The "Install patch" button downloads the delta, verifies it, and runs the helper; the app exits, applies, and restarts. The release page button remains for manual download if needed.
- If the helper fails, grab the full installer from the release page.

This keeps the existing per-push update model but switches downloads to a safe, file-level patch process that never overwrites the user's database or settings.
