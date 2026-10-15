# Runtime security backport — 2026-10-15

VS-1997 / Refs #787. Target `chore/tracking-automation`, base `6fdacdab44e7ec3db5a69cd1356685e7d6246a94`.
Only shared runtime/tooling and technical documentation change; existing release
metadata, application functionality and updater/archive contracts are retained.
The default-branch alert count is not marked fixed by this source change.
Protected identities remain subject to review and release qualification.
Rider tools are unavailable; fresh CLI results are required.

Fresh CLI Release: 924 Core tests, 114 script
tests, zero warnings/errors. Forced/no-cache NuGet audit of 5 projects
including transitive packages reports zero vulnerable packages. Actual win-x64
SDK download graphs resolve NETCore, ASP.NET Core and Windows Desktop 10.0.13;
the self-contained UI includes NETCore 10.0.13 and its deps runtime pack matches.
No unsupported framework is hidden behind a patched NETCore.

Full local logs: `/tmp/vaultsync-security-chore-tracking-automation-20261015-logs`. Runtime source hashes:

- `global.json`: `0018bf36cb15b92e9301d0b86a9a70455bd50b92881c602562873b71543e8036`
- `Directory.Build.props`: `bb3968d079510039442163ed5bd44cb68414bcb95c822e18cbbb54bf78167e4b`
- `Directory.Packages.props`: `751e861e47a02a927788b1b3854546a99b970c26e6ef555fd3efa6de74027336`
- `scripts/runtime_pack_audit.py`: `c62a740b06a6942aeddc5a176b195a8c0d5bbbdecb491bc5b7f1c149386714a6`
- `tests/scripts/test_runtime_pack_audit.py`: `da124b0d5c1956ac51291877828268fc7a55b4bfc93236716e2e59c534f1c3fa`

Managed advisory scans and runtime metadata are separate checks; native package
execution/upgrades and signed final artifacts remain release gates. Source
identity stays 1.8.9; no package is rebuilt or published here.

Localization: Legacy report only; --check unavailable; no UI changes.
