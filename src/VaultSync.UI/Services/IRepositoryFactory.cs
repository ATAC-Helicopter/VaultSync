using VaultSync.Core.Config;
using VaultSync.Core.Repositories;

namespace VaultSync.UI.Services;

public interface IRepositoryFactory
{
    SqliteRepository Create(AppConfig? config = null);

    string ResolveDbPath(AppConfig? config = null);
}
