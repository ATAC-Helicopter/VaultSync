# VaultSync Store Packaging

This folder contains the first Microsoft Store packaging scaffold for VaultSync.

Identity values currently wired from Partner Center:

- `Name`: `FlavioGiacchetti.480851279F98B`
- `Publisher`: `CN=D0FF8AE9-15EE-487F-B2F3-0913EFDA0CED`
- `PublisherDisplayName`: `Flavio Giacchetti`
- `Store ID`: `9N9HRX4JCLCP`

Current scope:

- separate Windows packaging layer for Microsoft Store distribution
- no changes to the Direct installer or GitHub updater path
- placeholder package assets copied from the current VaultSync branding preview
- manual Store upload package generation in `.github/workflows/release-assets.yml` with `include_store_upload` enabled

Important notes:

- this scaffold is intentionally not part of `VaultSync.sln` yet, so normal repo builds stay unchanged
- runtime distribution-channel detection is implemented; Store builds disable the GitHub updater and direct users to Microsoft Store updates
- Store-packaged filesystem, restore, removable-drive, and UNC/NAS behavior must be validated before submission
- restricted capabilities such as `broadFileSystemAccess` and `runFullTrust` may require review or adjustments during submission

Expected next steps:

1. build and inspect the 1.8.9 upload package
2. validate packaged app behavior for local folders, external drives, and network paths
3. replace placeholder Store package assets with final submission-ready sizes if needed
4. prepare Partner Center listing fields and restricted capability rationale
5. keep the submission checklist in `docs/MICROSOFT_STORE_SUBMISSION_CHECKLIST.md` current
