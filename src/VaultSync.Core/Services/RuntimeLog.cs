using System;
using VaultSync.Core.Config;

namespace VaultSync.Core.Services;

public static class RuntimeLog
{
#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    private static volatile bool _verboseEnabled;

    public static bool ShouldEmitVerbose => IsDebugBuild || _verboseEnabled;

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
