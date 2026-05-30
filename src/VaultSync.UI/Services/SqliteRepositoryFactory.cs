using VaultSync.Core.Config;
using VaultSync.Core.Repositories;

namespace VaultSync.UI.Services;

public sealed class SqliteRepositoryFactory(IAppConfigStore configStore) : IRepositoryFactory
{
    public SqliteRepository Create(AppConfig? config = null) =>
        new(ResolveDbPath(config));

    public string ResolveDbPath(AppConfig? config = null) =>
        configStore.ResolveDbPath(config);
}
