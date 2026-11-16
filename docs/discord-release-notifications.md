# VaultSync release notifications on Discord

VaultSync sends one Discord notification to **`#announcements`** when a GitHub release is published. Configure the `DISCORD_RELEASE_WEBHOOK` Actions secret with a webhook created in that channel. Each release post mentions only the opt-in **Dev Updates**, **Beta Updates**, and **Release Updates** roles; mentions in release notes remain disabled.

To announce an already-published release, run **VaultSync release to Discord** from the `Stable` branch and enter its tag, for example `v1.8.10`. The workflow rejects draft releases and tags outside VaultSync's version format.

The release workflow runs independently of asset builds and developer log posts. Asset CI may succeed or fail without affecting an announcement for an already-published GitHub release.
