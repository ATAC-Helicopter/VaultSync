# Runtime security servicing

Owner: VS-1997 / #787. All 18 runtime alerts reported on Stable require .NET
10.0.13; SDK 10.0.402 supplies the serviced reference/runtime packs. See
[Microsoft release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).

The source SDK floor is 10.0.402, runtime floor 10.0.13, and coordinated Sqlite,
ProtectedData and Drawing.Common packages use 10.0.13. Keep latest-patch
resolution. Do not globally override RuntimeFrameworkVersion: Windows SDK
reference packages use a different numeric version scheme.

The audit requires known, unique stable Microsoft NETCore/ASP.NET/Windows Desktop
included-framework versions on the configured major/minor line at or above
the floor. A patched NETCore cannot hide an old optional framework. Unknown,
duplicate, malformed, preview and unsupported-line entries fail closed.

Force a full NuGet advisory audit including transitive packages, build Release
with warnings as errors, run Core/script tests and inspect the actual Windows
self-contained runtimeconfig, deps and all three resolved runtime pack downloads.
Metadata checking supplements retained binary hashes/SBOM and signed provenance;
it does not prove that shipped payloads were not swapped.

These patches preserve application scope, archive/updater formats and source
release identity. Protected targets need reviewed merge commits; no bypass,
automatic merge, tag, asset replacement or publication is authorized. Published
1.8.9 binaries are not rebuilt under the same identity; any security distribution
needs an explicitly selected new release version and its actual qualification.
The sole assembled 1.9 release PRs remain unchanged: these are security backports.
The immutable archive snapshot and non-code download-stats branch are excluded.

Dependabot alerts remain open until the protected default Stable tree is patched
and GitHub reanalyzes it. Do not dismiss them because preparation branches pass.

Branch evidence: [current verification](release-evidence/runtime-security-2026-10-15.md).
