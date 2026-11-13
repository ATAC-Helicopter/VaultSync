# VaultSync 1.8.10 — Security and Dependency Maintenance (candidate)

This is a **pre-release preparation**, not a published update or announcement that any unverified fix has shipped.

## Source and isolation

- Source: published `v1.8.9` stable line, branch `release/1.8.10` created from `Stable`.
- Scope: serviced .NET 10 LTS runtime/SDK packages and qualified dependency updates only.
- Source release identity: `1.8.10`; previous stable: `1.8.9` (2026-09-16).
- Target planning date: 2026-11-14, not a commitment to release without evidence.
- No 1.9 recovery features, disk-cloning APIs, format migrations, or unrelated behavior changes.
- Do not add embargoed vulnerability details to public commits, logs, CI, or issue discussions. Coordinate private reports under SECURITY.md.

## Pending release gates

- [ ] Review all changed dependency lock/asset graphs on Windows, Linux and macOS; verify package availability.
- [ ] Qualify SkiaSharp 4.156.0 / HarfBuzzSharp 14.4.0.100 as a complete cross-platform managed/native group, including rendering, font shaping, graphics fallback, RAW/DNG codec expectations and binary ABI tests; revert the rendering group if any platform fails.
- [ ] Run targeted backup safety, verification, restore, updater and metadata regression tests with disposable fixtures.
- [ ] Validate final self-contained packages embed only serviced Microsoft runtimes, including optional framework references, at >=10.0.15.
- [ ] Verify updater's allowed 1.8.9 predecessor and full-installer fallback; do not infer patch compatibility from version strings alone.
- [ ] Perform quality/security gates and license/SBOM/package provenance verification on exact source SHA.
- [ ] Run release-candidate packages on all three OS platforms with rollback and smoke tests.
- [ ] Preserve protected review and final Stable-promotion/signing workflows; never claim release qualification from a passing build alone.
- [ ] Coordinate disclosure of any private vulnerability only after remediation and publication approval.
