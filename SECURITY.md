# Security Policy

## Reporting a Vulnerability
Report security issues privately.

Preferred channel:
- GitHub Security Advisory (private report)

Fallback channel:
- GitHub issue/discussion with minimal sensitive details, requesting private follow-up

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
