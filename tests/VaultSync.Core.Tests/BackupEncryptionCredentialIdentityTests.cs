using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupEncryptionCredentialIdentityTests
{
    [Fact]
    public void AccountName_UsesStableBackupEncryptionIdentity()
    {
        Assert.Equal("vaultsync-backup-encryption", BackupEncryptionCredentialIdentity.AccountName);
    }
}
