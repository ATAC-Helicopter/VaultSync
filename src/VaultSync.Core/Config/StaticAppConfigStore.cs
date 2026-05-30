using System.Threading;
using System.Threading.Tasks;

namespace VaultSync.Core.Config;

public sealed class StaticAppConfigStore : IAppConfigStore
{
    public static StaticAppConfigStore Instance { get; } = new();

    private StaticAppConfigStore()
    {
    }

    public bool WasConfigMissingOnFirstLoad => AppConfigStore.WasConfigMissingOnFirstLoad;

    public AppConfig GetSnapshot() => AppConfigStore.GetSnapshot();

    public AppConfig Load() => AppConfigStore.Load();

    public void Save(AppConfig config) => AppConfigStore.Save(config);

    public Task SaveAsync(AppConfig config, CancellationToken ct = default) =>
        AppConfigStore.SaveAsync(config, ct);

    public string GetDefaultDbPath() => AppConfigStore.GetDefaultDbPath();

    public string ResolveDbPath(AppConfig? config = null) => AppConfigStore.ResolveDbPath(config);
}
