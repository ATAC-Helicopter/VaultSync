using System;
using VaultSync.Core.Services;

namespace VaultSync.UI.Services;

public static class AppBuildInformationService
{
    private static readonly Lazy<BuildInformation> s_current = new(Create);

    public static BuildInformation Current => s_current.Value;

    private static BuildInformation Create()
    {
        AppDistributionInfo distribution = DistributionChannelService.Current;
        BuildInformation initial = BuildInformationService.Create(typeof(AppBuildInformationService).Assembly);
        if (!distribution.IsStore)
            return initial;

        return BuildInformationService.Create(
            typeof(AppBuildInformationService).Assembly,
            new BuildInformationOverrides(
                PackageKind: "microsoft-store-msix",
                UpdateSource: "microsoft-store"));
    }
}
