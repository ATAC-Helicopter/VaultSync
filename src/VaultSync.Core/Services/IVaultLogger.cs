namespace VaultSync.Core.Services;

public interface IVaultLogger
{
    void Verbose(string message);

    void Info(string message);

    void Warning(string message);

    void Error(string message);
}
