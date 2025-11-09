namespace VaultSync.CLI.Config
{
    sealed class AppConfig
    {
        public string Database { get; set; } = "~/.vaultsync/vault.db";
    }
}