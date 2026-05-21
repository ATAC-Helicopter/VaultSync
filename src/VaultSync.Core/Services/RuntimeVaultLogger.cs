using System;

namespace VaultSync.Core.Services;

public sealed class RuntimeVaultLogger : IVaultLogger
{
    public static RuntimeVaultLogger Instance { get; } = new();

    private RuntimeVaultLogger()
    {
    }

    public void Verbose(string message) => RuntimeLog.WriteVerbose(message);

    public void Info(string message) => Console.WriteLine(message);

    public void Warning(string message) => Console.WriteLine(message);

    public void Error(string message) => Console.WriteLine(message);
}
