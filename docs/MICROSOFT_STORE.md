# Microsoft Store

VaultSync has a reserved Microsoft Store identity and an initial packaging scaffold under `packaging/VaultSync.Store`.

Current reserved identity:

- `Identity Name`: `FlavioGiacchetti.480851279F98B`
- `Publisher`: `CN=D0FF8AE9-15EE-487F-B2F3-0913EFDA0CED`
- `Publisher Display Name`: `Flavio Giacchetti`
- `Package Family Name`: `FlavioGiacchetti.480851279F98B_e8epvg776k60t`
- `Store ID`: `9N9HRX4JCLCP`

Channel model:

- `Direct`
  - GitHub installer and updater stay enabled
- `Store`
  - Microsoft Store package and Store-managed updates
  - GitHub self-update must stay disabled

Current package/build entry points:

- Packaging project: `packaging/VaultSync.Store/VaultSync.Store.wapproj`
- Manifest: `packaging/VaultSync.Store/Package.appxmanifest`
- Manual package workflow: `.github/workflows/release-assets.yml` with `include_store_upload` enabled

Before submission, the following still need to be completed:

- packaged-app validation for local folders, removable drives, restore targets, and UNC/NAS paths
- Partner Center submission assets and compliance checklist

Current implementation status:

- done: initial Store packaging scaffold with reserved identity values
- done: runtime `Direct` vs `Store` channel detection
- done: Store builds disable the GitHub self-updater, show Store-managed update messaging, and offer an `Open Microsoft Store` action
- done: the release asset workflow has Store upload-package generation behind the `include_store_upload` option
- remaining: build and inspect the 1.8.7 Store upload artifact from GitHub Actions
- remaining: packaged-app validation for local folders, removable drives, restore targets, and UNC/NAS paths
- done: Store-specific update and support guidance is covered in the maintained documentation set

Compliance checklist:

- `docs/MICROSOFT_STORE_SUBMISSION_CHECKLIST.md`
