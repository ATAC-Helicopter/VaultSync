# Runtime security servicing — VaultSync 1.8.10

Owner: VS-1997 / [#787](https://github.com/ATAC-Helicopter/VaultSync/issues/787). The planned 1.8.10 release remains based on the published 1.8.9 stable source. This public document covers only already-disclosed .NET servicing information and the release build audit; it does not contain any privately reported product vulnerabilities.

The **November 10, 2026** serviced .NET 10 LTS baseline is **10.0.15** with **SDK 10.0.404**. Upgrade the SDK, bundled NETCore/ASP.NET Core/Windows Desktop runtimes, and the Microsoft.Data.Sqlite, ProtectedData and Drawing.Common packages together on the .NET 10 line. See the [official release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.15/10.0.15.md).

Never override a Windows SDK reference package with a .NET runtime patch. `TargetLatestRuntimePatch` remains enabled. The runtime pack auditor checks `runtimeOptions.includedFrameworks` for known unique Microsoft frameworks, exact stable versions, supported major/minor and versions at or above the configured minimum. A patched NETCore framework cannot hide an unpatched optional framework.

Required exact-source release gates include a no-cache direct/transitive NuGet audit, warning-as-error Release build, relevant C#/Python tests, every distributed RID self-contained runtimeconfig/asset inspection, installer/updater provenance, SHA-256 and SBOM verification, and native Windows/macOS/Linux smoke and rollback checks. A successful runtimeconfig check is necessary but not sufficient proof of safe shipped binaries. Private coordinated vulnerability reports stay private until fixed and publication is approved.

Do not replace the existing published 1.8.9 assets. Keep code, signing, protected review and final Stable promotion in their normal verified workflow. All 1.9 release-preparation branches remain distinct.
