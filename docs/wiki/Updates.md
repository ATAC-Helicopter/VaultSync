# Updates

The current published stable release is **1.8.10**, released November 15, 2026.
The **1.9** branch and its CLI commands are development work, not an available
stable update. Follow [development status](Development-Status.md) for the PR stack
and qualification gates. Upgrade qualification from an installed 1.8.10 to 1.9
is still pending. The planned FG Labs owner compatibility has not authorized a
repository transfer; official stable downloads remain in this repository.

VaultSync supports two Windows update models:
- Direct builds use GitHub patch and installer updates.
- Microsoft Store builds use Store-managed updates.

Always use the [official GitHub Releases page](https://github.com/ATAC-Helicopter/VaultSync/releases/latest) to identify the latest published stable version.

## Distribution models

### Direct downloads

Direct builds for Windows, macOS, and Linux use GitHub release metadata to discover available updates.

Depending on platform, version, and published assets, VaultSync may offer:

- A compatible patch update.
- A full installer or package.
- A manual download when an automatic update is unavailable.

### Microsoft Store

Microsoft Store builds use Store-managed delivery rather than the GitHub patch or installer flow.

Use the Microsoft Store to manage updates for this distribution.

Do not mix the Store and Direct installation channels in place.

## Release channels

- **Stable:** published releases recommended for regular use.
- **Beta:** explicitly published test releases for early feedback.

Switch supported channels through **Settings > Advanced**.

A development branch, planned milestone, or unpublished release is not an available update.

## Checking for updates

1. Open **Settings**.
2. Navigate to **Advanced**.
3. Select **Check for updates now**.
4. Review the displayed release channel and update status.

Microsoft Store builds direct users to the Store rather than offering the GitHub installer workflow.

![Maintenance and update controls in Settings](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/images/Settings_Maintenance.png)

## Patch update eligibility

Patch updates are offered only when the published patch manifest explicitly permits the installed version.

VaultSync uses numeric version comparison. For example, `1.8.10` is newer than `1.8.9`, but that comparison alone does not establish patch compatibility or release availability.

The patch manifest must list the installed version as an allowed base version.

### Exact version allowlists

A single target release can support more than one exact predecessor.

These are strict allowlists, not version ranges.

A predecessor is eligible only when its published managed-file inventory satisfies the release pipeline's compatibility requirements for the target payload.

Eligibility is evaluated independently for each supported platform.

### When a patch is unavailable

Use the appropriate full installer or package when:

- Your installed version is not in the manifest allowlist.
- You are upgrading across an unsupported version gap.
- Patch preflight blocks the operation.
- Required or trusted patch assets are unavailable.
- The current platform or installation method does not support that patch.

A newer release being discoverable does not guarantee a compatible patch.

## Patch assets and verification

Published patch assets use platform-specific manifest and ZIP files.

The general naming pattern is:

- `vaultsync-patch-<platform>.json`
- `vaultsync-patch-<platform>.zip`

Where applicable, macOS checks architecture-specific patch assets before generic ones, including Apple Silicon and Intel variants.

Linux similarly checks supported x64 and ARM64 variants.

Direct updates verify required trusted release metadata, including payload size and SHA-256 digests. Missing or mismatched trusted values block the affected update.

Updater preflight diagnostics help explain why patching is allowed or blocked.

## Platform-specific behavior

### Windows

Direct releases use an Inno Setup `.exe` installer.

Microsoft Store packages follow the Store update mechanism.

### macOS

Direct releases use architecture-specific `.dmg` packages containing the canonical `VaultSync.app` and an Applications shortcut.

The 1.8.7 update path includes a one-time migration from the exact 1.8.6 predecessor to the canonical `/Applications/VaultSync.app` location.

Older or unqualified installations should use the corresponding full DMG.

Later qualified patches update the canonical application bundle and verify its metadata.

### Linux

Published assets may include `.AppImage`, `.deb`, and `.tar.gz`, depending on architecture.

For Debian-package updates, VaultSync waits for the administrator-authentication and package-installation result. Cancelling authentication leaves the existing app running.

After successful installation, the replacement application starts only after the old process exits.

AppImage updates use a similar deferred handoff to avoid conflict with the running instance.

## Skipping a version

Use **Skip version** in the update banner if you do not want to install that version.

You can clear the skipped-version setting later to allow the banner to appear again.

## Troubleshooting

## Safe update expectation
- Use only assets from the official `ATAC-Helicopter/VaultSync` release page and compare their published SHA-256 digests before bypassing SmartScreen or Gatekeeper warnings.
- VaultSync rejects direct-update payloads whose trusted GitHub digest or exact size is missing or mismatched.
- Use patch updates only for versions explicitly supported by the release manifest.
- Use the installer for:
  - major version jumps
  - very old versions
  - blocked or incompatible patch preflight results
- For Microsoft Store builds, use the Store listing and Store app update flow instead of the GitHub installer.

- [Complete Recovery Horizon release plan](Release-Plan.md): CLI ownership and immutable BSC ID crosswalk.

## Release grouping and dependency audit — 2026-10-03

One open draft PR per release now includes its preparation and corrections;
see [Release Plan](Release-Plan.md). Dependabot #661/#720 merged as verified
maintenance; rendering/CLI compatibility fixes are assembled in 1.9.0 and
propagated to later implemented CLI variants. Unused Inter packaging is removed.
[Development status](Development-Status.md) and the linked daily ledger record
full changelog ownership, dependency consumers and remaining qualification gates.

## Release provenance verification

Candidate, beta and stable release preparation verifies GitHub/Sigstore provenance
signatures for the canonical manifest and every declared asset, including patch
JSON and ZIP. Proof is retained outside the release asset inventory. Updater
schema, asset names and byte-integrity checks remain compatible; desktop clients
do not verify Sigstore at runtime. Native publisher signing and notarization
remain separate from provenance, and unsigned distribution metadata stays
truthful. See [release signing policy](../RELEASE_SIGNING.md).
