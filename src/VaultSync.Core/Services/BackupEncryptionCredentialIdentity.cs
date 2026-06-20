namespace VaultSync.Core.Services;

public static class BackupEncryptionCredentialIdentity
{
    public static string AccountName { get; } = string.Join('-', "vaultsync", "backup", "encryption");
}
