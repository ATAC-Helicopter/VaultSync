using VaultSync.Core.Services;

namespace VaultSync.CLI.Utils;

/// <summary>
/// Keeps shared-service diagnostics out of command stdout, including quiet and JSON flows.
/// </summary>
internal sealed class CliVaultLogger : IVaultLogger
{
    public static CliVaultLogger Instance { get; } = new();

    private CliVaultLogger()
    {
    }

    public void Verbose(string message)
    {
        if (RuntimeLog.ShouldEmitVerbose)
            Log.Info(message);
    }

    public void Info(string message) => Log.Info(message);

    public void Warning(string message) => Log.Warn(message);

    public void Error(string message) => Log.Error(message);
}
