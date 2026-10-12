using System;
using System.Threading;
using VaultSync.Core.Config;

namespace VaultSync.Core.Services;

public static class RuntimeLog
{
    private static volatile bool _verboseEnabled;
    private static readonly AsyncLocal<IVaultLogger?> ScopedLogger = new();
    private static readonly bool ForceVerbose =
        string.Equals(Environment.GetEnvironmentVariable("VAULTSYNC_FORCE_VERBOSE"), "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("VAULTSYNC_FORCE_VERBOSE"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldEmitVerbose => ForceVerbose || _verboseEnabled;

    public static IDisposable UseLogger(IVaultLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        IVaultLogger? previous = ScopedLogger.Value;
        ScopedLogger.Value = logger;
        return new LoggerScope(previous);
    }

    public static void UpdateFromConfig(AppConfig? config)
    {
        AdvancedConfig? advanced = config?.Advanced;
        _verboseEnabled =
            advanced?.VerboseLogging == true ||
            advanced?.SaveVerboseLogs == true;
    }

    public static void WriteVerbose(string message)
    {
        if (ShouldEmitVerbose)
        {
            IVaultLogger? logger = ScopedLogger.Value;
            if (logger is null || ReferenceEquals(logger, RuntimeVaultLogger.Instance))
                Console.WriteLine(message);
            else
                logger.Verbose(message);
        }
    }

    public static void WriteWarning(string message)
    {
        IVaultLogger? logger = ScopedLogger.Value;
        if (logger is null || ReferenceEquals(logger, RuntimeVaultLogger.Instance))
            Console.WriteLine(message);
        else
            logger.Warning(message);
    }

    private sealed class LoggerScope(IVaultLogger? previous) : IDisposable
    {
        public void Dispose() => ScopedLogger.Value = previous;
    }
}
