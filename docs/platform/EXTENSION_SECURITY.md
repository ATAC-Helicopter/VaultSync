# RFC-P03 — Extension Trust, Isolation, Marketplace and Distribution

**Status:** Security research / candidate controls. No third-party extension runtime exists by authority of this document.  
**Scope:** Candidate VaultSync 2.0; 1.9.x may adopt only narrowly relevant **internal** safety contracts under [FOUNDATIONS_1_9.md](FOUNDATIONS_1_9.md).  
**Never change:** 1.9.0 release contract or beta gate.

## 1. Security objective

The application protects irreplaceable data. An extension must not be able to:
- read arbitrary source/backup files or secret storage;
- erase, corrupt, re-encrypt, truncate, hide, or silently redirect recovery points;
- bypass destination identity, preflight, write stages, retention holds, repository leases or restore consent;
- claim success/verification/recoverability without trustworthy byte-level evidence;
- exfiltrate content, absolute paths, credentials, encryption keys or non-consented diagnostics;
- persist after revocation, silently elevate privileges or execute commands through the trusted process;
- block emergency offline recovery by making supported data dependent on the extension catalog.

A curated marketplace and signed metadata protect *publication integrity*, **not** against all malicious or flawed source code. Every published update is a new binary requiring a new security and functional decision.

## 2. Threat actors and protected assets

| Actor | Plausible attack | Required mitigation |
| --- | --- | --- |
| Malicious extension author | exfiltration, destructive operation, fake evidence | OS-level sandbox, denied capabilities, broker and trusted proof |
| Compromised developer account | pushes compromised update | independent maintainer review, provenance, immutable artifact identity |
| Compromised publication account | swaps manifests, rollback/freeze | independent root keys, TUF-class metadata, offline root rotation |
| Hostile backup/archive metadata | traversal, symlinks, zip bombs, identity confusion | path containment, quotas, bounded decompression, duplicate rejection |
| Compromised local user/process | rewrites plugin folder/IPC | filesystem ownership, authenticated local pipe, installer integrity |
| Compromised provider / NAS | stale list, incomplete multipart, forged success | explicit backend capabilities and readback/evidence checks |
| Lost network / plugin | missing binaries/catalog | stable offline recovery and clear unsupported states |
| Buggy but approved extension | hangs, crashes, memory leaks, wrong responses | quotas, timeouts, cancellation, restart isolation and kill switch |
| Misconfigured user grants | excessive scope | narrow scopes, per-extension review, permission diffs |

Threat assumptions: malware with full administrator/root privileges is generally outside the achievable containment guarantees of an ordinary desktop app; do not claim isolation from a hostile privileged OS. Privileged disk imaging is first-party-only.

## 3. Isolation architecture

### Default-deny, two layers

1. **Operating system boundary**: plugin code in a separate, restricted process or another demonstrated sandbox runtime, without broad home/network/device access.
2. **VaultSync capability broker**: explicit host-mediated operations. Permissions in a manifest are *requests*, not actual OS enforcement.

The worker receives only opaque resource IDs and one-time scoped handles. Where possible it operates over controlled streams/chunks rather than arbitrary raw paths. The host checks the principal, scope, operation, maximum bytes, destination, deadline and cancellation. The broker rejects unsupported requests before touching user data.

Important: `AssemblyLoadContext` provides assembly-loading isolation, **not a security boundary**. It runs with the host process permissions. Do not load arbitrary third-party assemblies into the UI/Core.

### Platforms to prototype

| Platform/channel | Candidate isolation | Mandatory validation |
| --- | --- | --- |
| Windows direct installer | AppContainer/Windows sandboxed process + broker, named pipes with ACLs | packaging, restricted token, filesystem/network denial, permissions, crash/escape cases |
| Windows Microsoft Store | Store-compliant deployment, process/package identity and applicable sandbox controls | explicit policy review, Store certification, extension install/update path and rejection handling |
| macOS direct DMG | dedicated restricted helper, app sandbox/signing strategy and authenticated IPC where supported | current signing/notarization feasibility, helper entitlement scope, Gatekeeper, update and quarantine |
| macOS Mac App Store (hypothetical) | **separately evaluated product** | App Review 2.4.5 and 2.5.2 strongly restrict downloading executable functionality; do not promise executable marketplace support |
| Linux direct packages | user namespace / seccomp / Landlock / portal or container depending on host capabilities | distro/kernel variance, sandbox feature probing, mounts, privilege drops, filesystem and network denial |
| Linux Flatpak (hypothetical) | manifest portals + sandbox rules | dedicated packaging qualification and extension deployment policy |

**Process isolation alone is not sufficient.** A child process may inherit broad filesystem/network access. A child launched without an enforced sandbox is **not** a trustworthy untrusted-plugin host. Feature probing must fail closed: incompatible OS means extension unavailable, not unsandboxed fallback.

Research platforms are not implementation commitments. Compatibility varies by kernel, application packaging, CPU architecture and system policy.

### WebAssembly/WASI option

Investigate a WASI-compatible runtime for low-risk, bounded computation and declarative transformations. Positive property: host-selected imports and preopened resources can sharply constrain access. Open questions: async IO, streaming huge backups, encryption interfaces, .NET developer experience, native dependencies, resource meters and cross-platform reproducibility. **Do not make WASI the sole SDK technology without a qualifying POC.**

## 4. Permission model proposal

Names below are candidate authorization vocabulary, not public manifest API:

| Permission | Scope | Default |
| --- | --- | --- |
| `project.metadata.read` | selected project opaque metadata fields | deny |
| `recovery.evidence.read` | bounded redacted evidence | deny |
| `backup.content.read` | selected bytes, owner-approved, via host stream | deny |
| `destination.objects.read` | selected destination/prefix | deny |
| `destination.objects.write` | bounded staging prefix; host-committed only | deny |
| `network.connect` | approved endpoints/protocols only | deny |
| `notification.publish` | local user notification channel | deny |
| `report.export` | user-chosen output target only | deny |

No community permission to obtain plaintext encryption keys, OS credential references, raw DB write access, arbitrary restore/wipe, protected-retention bypass, repository lease takeover, or trusted proof attestation.

User-facing permission changes require reconfirmation, especially read-content, write-destination and network egress. Per-operation approval may be required even when installed. Installation is not blanket consent for reading files later.

## 5. Credential handling

- Credentials live in the platform's trusted secret mechanism. An extension sees **scoped broker tokens** or receives only an authenticated operation result when feasible.
- If a third-party provider protocol genuinely requires secret material in its isolated worker, make that high-risk permission explicit and gate it separately. Use short-lived, narrowly scoped credentials and never permanent global secrets by default.
- Credentials and tokens never appear in catalog metadata, CLI JSON, portable repository records, event payloads, crash bundles or automatic support diagnostics.
- Strict redaction/size limits apply to logs, worker stderr and exception messages. Human-reviewed export remains the default privacy policy.
- Revocation terminates worker sessions, invalidates tokens and blocks further broker operations.

## 6. Marketplace trust model

Separate parties and trust roles: source author, CI builder, security reviewers, release approver, artifact repository and client verifier. One compromised personal credential should not trivially authorize publication of arbitrary executable code.

Suggested pipeline:

```text
developer proposal + repository
 -> identity / license / rights check
 -> pinned dependencies, SBOM, static checks, secret scan
 -> reproducible or independently rebuildable package
 -> malicious fixtures and behavioral tests, multi-OS sandbox test
 -> human permissions + source/diff review (independent approval)
 -> immutable artifact + digest + provenance + signature
 -> independently signed/delegated repository metadata
 -> versioned catalog publication / transparency
 -> client verification before staged install
 -> enable by specific consent -> monitored local lifecycle
```

Every version re-enters the gate. Support scheduled review for unpublished builds and disclosure handling; do not promise unattended 24-hour human review without actual staffing.

### Signed metadata and update security

Evaluate **The Update Framework (TUF)** for threshold-signed roots, delegated targets, metadata expiry, rollback/freeze/mix-and-match protection and revocation. Verify client metadata before trusting artifact digest. Sigstore/SLSA-style build provenance may supplement, but must not be confused with source review or isolated execution.

Target security properties:
- embedded or safely bootstrapped offline trust root;
- separated offline root signing from routine publication keys;
- key rotation, threshold approval and delegated scopes;
- rollback prevention and expiry handling without lying about offline status;
- pin installed package digest and exact version; no same-version mutation;
- deny changed permission set until user re-approves;
- transactional staged installation, restart-safe commit, rollback to last trusted version if safe;
- revocation: stop execution and future installs; preserve backup bytes and minimal required metadata;
- emergency key-compromise response with public audit trail and tested client update.

Current VaultSync app packages are documented as unsigned direct-download artifacts with official digests and attestations. **Do not assert** those controls alone are already sufficient for arbitrary untrusted plugin installation.

### Review levels (candidate)

- `declarative`: schema/rule validation and documented testing; no executable payload.
- `read-only`: sandbox and broker validation, redacted evidence only.
- `data-reader`: heightened review, byte-scope grants, exfiltration testing.
- `provider-writer`: security review, credential handling, network policy, corruption/interruption qualification, clean-machine recovery.
- `privileged`: **not publishable as third-party community plugin** without a future separate trusted first-party model.

## 7. Manifest conceptual fields

```json
{
  "schemaVersion": "proposal-only",
  "id": "org.example.report",
  "version": "0.1.0",
  "publisherId": "publisher-identity",
  "entrypointType": "isolated-worker",
  "hostProtocolRange": "to-be-defined",
  "runtimes": ["win-x64", "linux-x64", "osx-arm64"],
  "requestedCapabilities": ["recovery.evidence.read"],
  "dataFormats": [],
  "dependencies": [],
  "artifactDigest": "sha256:..."
}
```

This is illustrative and **not** a schema or approved installation format. Do not hardcode its shape in 1.9. Plugin ID is not a trust identity; signatures and separate publisher verification are required.

## 8. Distribution policy matrix

- **Windows direct distribution:** independent extension downloader is technically imaginable, but enforcement and installer trust must be qualified.
- **Windows Microsoft Store:** review current Store certification rules before allowing dynamically installed executable capability. Do not assume direct distribution rules apply.
- **macOS direct distribution:** determine whether executable helper signing, hardened runtime, notarization and quarantine allow the proposed lifecycle. A packaging workaround is not a security model.
- **Mac App Store:** Apple App Review Guidelines 2.4.5(iv), 2.5.2 prohibit downloading/installing additional executable functionality in the ordinary case. Do not promise an executable extension marketplace on this channel; prefer bundled approved features and safe non-code content only if review permits.
- **Linux:** distros, AppImage, deb, tar and future sandboxed formats have different file ownership, execution permissions and isolation options; validate separately.
- **Offline recovery medium:** no catalog network or plugin installation required to open official supported formats.

A different feature matrix per channel is acceptable if clearly communicated. Never bypass store policy by silently loading unsigned code or claiming that declarative metadata is harmless when it can trigger privileged behavior.

## 9. Security release and incident operations

1. Receive private vulnerability report; isolate impact and preserve evidence.
2. Assign severity, affected extension versions, exposed capabilities and recovery risk.
3. Revoke/suspend affected release, inform clients, block activation, terminate running worker where possible.
4. Publish verified fix, inspect downstream dependencies, rotate relevant signing keys/tokens.
5. Help users recover **without mutating existing backup payloads**.
6. Publish appropriately timed advisory; document whether historical data may have been exfiltrated.
7. Exercise response in drills: compromised publisher, compromised online metadata key, malicious update, split-brain metadata, offline expired timestamp, silent network failure.

## 10. Security qualification gate

A prototype is not safe until adversarial tests demonstrate:
- denied arbitrary filesystem reads, credential access and unapproved network egress on Windows/macOS/Linux;
- no ability to authorize destructive core operations or attest proof;
- worker crash/hang/process kill does not corrupt active backups, staged restores, leases or BSC refs;
- malicious zip/manifest/IPC fuzzing is bounded by size/time/memory quotas;
- unsigned, tampered, revoked, downgraded, dependency-confused and expired packages fail closed;
- offline clean-machine recovery succeeds with no plugin/catalog;
- plugin disable/removal does not delete payloads or fabricate healthy status;
- permission changes have explicit review and auditable local record.

**No API, permission or package format proposed here is approved for shipping.**

## Primary official references

- Microsoft [.NET AssemblyLoadContext](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext): not a security feature.
- Microsoft [AppContainer isolation](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation) and [sandbox process API](https://learn.microsoft.com/en-us/windows/win32/secauthz/createprocessinsandbox).
- Apple [App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/) 2.4.5, 2.5.2 and [App Sandbox](https://developer.apple.com/documentation/security/app-sandbox).
- [The Update Framework specification](https://theupdateframework.github.io/specification/) for signed metadata and rollback/freeze protections.
- VaultSync [Security](../../SECURITY.md), [Privacy](../PRIVACY.md), [Updater](../UPDATER.md), [Repository Formats](../REPOSITORY_FORMATS.md).
