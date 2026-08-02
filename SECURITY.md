# Security Policy

## Reporting a Vulnerability
Report security issues privately.

Preferred channel:
- GitHub Security Advisory (private report)

Fallback channel:
- Email the maintainer listed on the repository owner's GitHub profile

Do not open a public issue or discussion for a suspected vulnerability.

Please include:
- affected version
- reproduction steps
- impact summary
- any suggested mitigation

## Response Policy
- We acknowledge valid reports as soon as possible.
- We triage severity and provide an estimated remediation window.
- We publish fixes in release notes after remediation.

## Supported Versions
Security fixes target the latest stable release.
Backports to older versions are best-effort and depend on risk and implementation cost.

## Scope Notes
- Do not post exploit details publicly before a fix is available.
- Non-security bugs should be reported through `docs/wiki/Reporting-Bugs.md`.
- Backup encryption setup, behavior, and format are documented in `docs/wiki/Encryption.md`.
- Patch-update compatibility is intentionally strict:
  - VaultSync only applies patch manifests to explicitly listed base versions
  - unsupported or older versions must use the installer fallback

## Distribution Integrity

Direct-download desktop packages are intentionally unsigned because paid
platform signing and notarization programs are outside the project's supported
budget. Do not interpret a direct package as publisher-signed or notarized.

Compensating controls are mandatory for supported releases:

- obtain packages only from the official `ATAC-Helicopter/VaultSync` release;
- verify the SHA-256 digest published for the asset before bypassing an
  operating-system warning;
- updater installer, manifest, and archive downloads must match trusted GitHub
  digests and exact sizes before use;
- updater verification failures must fail closed without executing or applying
  the downloaded payload;
- Windows SmartScreen and macOS Gatekeeper warnings must remain documented.

Release-asset digests protect against corruption and substitution outside the
official release account. They do not replace independent signing if the GitHub
account or release process itself is compromised.
