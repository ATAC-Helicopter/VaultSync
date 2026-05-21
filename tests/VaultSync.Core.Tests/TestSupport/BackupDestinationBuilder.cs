using VaultSync.Core.Config;

namespace VaultSync.Core.Tests.TestSupport;

public sealed class BackupDestinationBuilder
{
    private string _path = @"C:\Backups";
    private string _alias = "Primary";
    private bool _active = true;
    private string _credentialName = string.Empty;
    private bool _preMounted;

    public BackupDestinationBuilder WithPath(string path)
    {
        _path = path;
        return this;
    }

    public BackupDestinationBuilder WithAlias(string alias)
    {
        _alias = alias;
        return this;
    }

    public BackupDestinationBuilder WithCredentialName(string credentialName)
    {
        _credentialName = credentialName;
        return this;
    }

    public BackupDestinationBuilder Active(bool active = true)
    {
        _active = active;
        return this;
    }

    public BackupDestinationBuilder PreMounted(bool preMounted = true)
    {
        _preMounted = preMounted;
        return this;
    }

    public BackupDestination Build() =>
        new()
        {
            Path = _path,
            Alias = _alias,
            Active = _active,
            CredentialName = _credentialName,
            PreMounted = _preMounted
        };
}
