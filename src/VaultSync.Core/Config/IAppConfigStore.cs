using System.Threading;
using System.Threading.Tasks;

namespace VaultSync.Core.Config;

public interface IAppConfigStore
{
    bool WasConfigMissingOnFirstLoad { get; }

    AppConfig GetSnapshot();

    AppConfig Load();

    void Save(AppConfig config);

    Task SaveAsync(AppConfig config, CancellationToken ct = default);

    string GetDefaultDbPath();
}
