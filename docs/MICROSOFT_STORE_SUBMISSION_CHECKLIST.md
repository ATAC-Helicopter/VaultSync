# Microsoft Store Submission Checklist

This checklist tracks the concrete work needed before VaultSync can be submitted to Microsoft Store with confidence.

Reference:
- Microsoft Learn: `Create an app submission for your MSIX app`

## Done
- Reserved Partner Center product name and captured Store identity values.
- Added Store packaging scaffold under `packaging/VaultSync.Store`.
- Wired the reserved identity values into `Package.appxmanifest`.
- Added runtime `Direct` vs `Store` channel detection.
- Disabled GitHub self-update for Store builds.
- Added `Open Microsoft Store` action and Store-managed update messaging.
- Added Microsoft Store upload package generation to `.github/workflows/release-assets.yml` behind the `include_store_upload` option.

## Needs code / packaging validation
- Build the 1.8.6 Store upload package in GitHub Actions with `include_store_upload` enabled and verify the artifact shape (`.msixupload` or equivalent upload package).
- Install the packaged build and validate:
  - local folder backup
  - local restore
  - removable-drive backup destination
  - UNC / NAS backup destination
  - helper-tool execution
- Confirm notifications, tray behavior, and shell integration still behave correctly in packaged mode.
- Confirm support bundle output still works in packaged mode.

## Needs Partner Center submission data
- Pricing and availability:
  - markets
  - audience
  - discoverability
  - release schedule
- Properties:
  - category
  - subcategory if needed
  - support/contact details
  - privacy policy URL if required
- Age ratings:
  - complete all required questionnaire answers
- Store listings:
  - description
  - what is new
  - screenshots
  - Store logos / hero art if required
  - copyright / trademark info if used
- Submission options:
  - certification notes
  - restricted capability justification

## Restricted capability review notes
Current manifest capabilities:
- `runFullTrust`
- `broadFileSystemAccess`
- `internetClient`
- `privateNetworkClientServer`

Store review prep still needed:
- justify why `runFullTrust` is required for VaultSync's desktop backup workflows
- justify why `broadFileSystemAccess` is required for project-folder backup and restore workflows
- document any fallback or degraded behavior if a capability is limited during review

## Submission assets still needed
- Final Store screenshots sized and curated for Partner Center
- Final Store listing copy
- Capability rationale text
- Privacy/compliance notes
- Submission checklist review pass for the exact release being submitted

## Release gate
VaultSync is only Store-submission ready when:
- the package builds reproducibly
- the packaged app passes filesystem / restore / NAS validation
- Partner Center listing fields are complete
- restricted capability justification is prepared
