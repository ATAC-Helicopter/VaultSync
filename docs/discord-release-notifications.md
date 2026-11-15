# VaultSync release notifications on Discord

VaultSync sends one Discord notification when a GitHub release is published. The workflow reads the public release notes from the VaultSync GitHub release and uses the `DISCORD_RELEASE_WEBHOOK` Actions secret. Mentions are disabled in release text.

To announce an already-published release, run **VaultSync release to Discord** from the `Stable` branch and enter its tag, for example `v1.8.10`. The workflow rejects draft releases and tags outside VaultSync's version format.

The release workflow runs independently of asset builds and developer log posts. Asset CI may succeed or fail without affecting an announcement for an already-published GitHub release.
