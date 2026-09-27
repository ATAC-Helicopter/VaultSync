# FG Labs ownership migration

VaultSync remains at `ATAC-Helicopter/VaultSync` until an updater compatibility release is distributed. The existing 1.8.9 updater pins that owner in both the manifest descriptor and asset metadata. GitHub changes `browser_download_url` to the organization owner immediately after transfer, so redirects alone cannot preserve its validation contract.

The compatibility change accepts only the two explicit identities `ATAC-Helicopter/VaultSync` and `FGLabs-dev/VaultSync`. Historical manifest URLs can be compared with post-transfer API metadata for the same exact tag and asset name. Byte size, SHA-256 digest, schema, predecessor and release identity checks remain enforced. No existing release assets are rewritten.

Before transferring:

1. Qualify and distribute a compatibility release under the personal repository.
2. Verify upgrade from an existing installed release on every supported platform.
3. Allow adoption of that release; clients older than it require manual upgrade after transfer.
4. Transfer the repository, preserve its visibility, rules, releases, IDs and default branch, then update canonical website, build and repository URLs.
5. Verify the real post-transfer API and existing historical manifests, updater download/install flow, Pages URL, and GitHub Project links.

The compatibility patch is preparation, not evidence that a release has shipped or that existing installed clients have adopted it.
