using System;
using VaultSync.Core.Config;

namespace VaultSync.Core.Services;

public static class RuntimeLog
{
    private static volatile bool _verboseEnabled;
    private static readonly bool ForceVerbose =
        string.Equals(Environment.GetEnvironmentVariable("VAULTSYNC_FORCE_VERBOSE"), "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("VAULTSYNC_FORCE_VERBOSE"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldEmitVerbose => ForceVerbose || _verboseEnabled;

    public static void UpdateFromConfig(AppConfig? config)
    {
        var advanced = config?.Advanced;
        _verboseEnabled =
            advanced?.VerboseLogging == true ||
            advanced?.SaveVerboseLogs == true;
    }

    public static void WriteVerbose(string message)
    {
        if (ShouldEmitVerbose)
        {
            Console.WriteLine(message);
        }
    }
}
