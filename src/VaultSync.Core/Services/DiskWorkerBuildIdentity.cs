using System.Reflection;
using System.Text.Json;

namespace VaultSync.Core.Services;

internal static class DiskWorkerBuildIdentity
{
    internal const string Argument = "--installation-identity";
    internal static string? SourceCommit
    {
        get
        {
            string? value = typeof(DiskWorkerBuildIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+').LastOrDefault();
            return value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? value : null;
        }
    }
    internal static byte[] Encode() => SourceCommit is { } commit
        ? JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, role = DiskWorkerInstallationManifest.Role,
            runtimeIdentifier = "linux-x64", sourceCommit = commit })
        : throw new InvalidDataException("worker-installation.build-unidentified");
}
