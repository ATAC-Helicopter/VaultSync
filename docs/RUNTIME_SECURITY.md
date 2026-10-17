# Runtime servicing and release verification

Owner: VS-1997 / [#787](https://github.com/ATAC-Helicopter/VaultSync/issues/787).
The October 13, 2026 security release requires .NET 10.0.13 or a later serviced
patch on the same runtime line. SDK 10.0.402 includes the corrected runtime
([Microsoft release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)).
This contract applies to all open preparation PRs using the shared .NET 10
packaging foundation; it does not change release ownership or feature scope.

## Inputs

- `global.json` requires SDK 10.0.402, preserving the existing roll-forward policy.
- `Directory.Build.props` declares `VaultSyncMinimumRuntimeVersion` 10.0.13,
  and retains `TargetLatestRuntimePatch`. The serviced SDK supplies per-framework
  runtime versions; explicit overrides are still checked in output. A global
  `RuntimeFrameworkVersion` must not force .NET patch numbers onto Windows SDK
  reference packages, which have a different version scheme.
- Central Microsoft.Data.Sqlite, ProtectedData and Drawing.Common packages are
  coordinated on 10.0.13. Managed package audits and embedded runtime audits
  answer different questions; both are necessary.
- Use actual release RID/self-contained outputs. A generic local build or a
  current system runtime does not establish what an installer bundles.

## Verification

`scripts/runtime_pack_audit.py --runtimeconfig PATH` requires self-contained
`includedFrameworks` metadata containing Microsoft.NETCore.App. Every included
framework must be a known Microsoft framework, appear exactly once and have a
stable version on the configured major/minor runtime line at or above the floor.
It checks NETCore, ASP.NET Core and Windows Desktop separately; a patched NETCore
must not hide an old optional framework. Unknown names, prereleases, malformed
versions and duplicate entries fail closed. Preview or future runtime lines need
their own reviewed servicing policy, not a numeric bypass.

The existing release jobs audit Windows, macOS and Linux self-contained metadata;
required Linux CI also audits its publish. Actual per-RID runtime pack graphs,
package advisory audits and artifact inspection supplement this metadata check.
Metadata alone cannot establish that the corresponding shipped binaries were
not swapped, so retain canonical file hashes, SBOM and signed provenance.

Do not dismiss default-branch alerts merely because a preparation branch is
updated. Do not publish an unserviced runtime or weaken updater verification to
make a release pass. Runtime servicing does not change archive formats or updater
manifest/provenance contracts. Qualify installed update and restore flows using
the actual serviced packages before promotion.

## Advisory scope and historical evidence

GitHub reported 18 default-branch runtime advisories when compression work was
delivered, including two critical ASP.NET Core advisories:
[GHSA-rv87-ccxw-8ggf](https://github.com/advisories/GHSA-rv87-ccxw-8ggf) and
[GHSA-fpq8-4vhr-rcxx](https://github.com/advisories/GHSA-fpq8-4vhr-rcxx).
Their corrected .NET 10 version is 10.0.13. Dependency presence alone does not
prove that VaultSync exercises a vulnerable server/authentication path. No direct
Kestrel/WebApplication/cookie-auth code was found in the Core/UI source audit;
we still require serviced runtime artifacts and assess all alerts.

The earlier October 13 dependency audit preceded these advisories and is
historical evidence. Current output and per-head results are recorded separately
in [runtime servicing evidence](release-evidence/1.9-runtime-servicing-2026-10-14.md).

## All maintained source branches — 2026-10-15

The requested inventory includes Stable, Dev, all protected release identities
and open maintenance/dependency PR heads, not only the nine assembled release
preparations. Scope, source hashes, fresh package/runtime graph audits and each
reviewed-integration candidate are in
[the current branch ledger](release-evidence/1.9-runtime-branches-2026-10-15.md).
Protected sources and the default alert count remain unpatched until their
required PR review/merge. Historical archives and non-code branches retain their
declared roles. Never equate a prepared fix with a closed default-branch alert
or replace a published package under the same version.
