using System;
using System.Runtime.InteropServices;

namespace VaultSync.UI.Services;

public enum AppDistributionChannel
{
    Direct,
    Store
}

public sealed record AppDistributionInfo(
    AppDistributionChannel Channel,
    bool IsPackaged,
    string PackageFamilyName,
    string PackageFullName,
    string DetectionSource)
{
    public bool IsStore => Channel == AppDistributionChannel.Store;
}

public static class DistributionChannelService
{
    public const string StorePackageFamilyName = "FlavioGiacchetti.480851279F98B_e8epvg776k60t";
    private const int AppModelErrorNoPackage = 15700;

    private static readonly Lazy<AppDistributionInfo> s_current = new(Detect);

    public static AppDistributionInfo Current => s_current.Value;

    private static AppDistributionInfo Detect()
    {
        var overrideValue = Environment.GetEnvironmentVariable("VAULTSYNC_DISTRIBUTION_CHANNEL")?.Trim();
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            if (string.Equals(overrideValue, "store", StringComparison.OrdinalIgnoreCase))
            {
                return new AppDistributionInfo(
                    AppDistributionChannel.Store,
                    IsPackaged: true,
                    PackageFamilyName: StorePackageFamilyName,
                    PackageFullName: string.Empty,
                    DetectionSource: "env:VAULTSYNC_DISTRIBUTION_CHANNEL");
            }

            if (string.Equals(overrideValue, "direct", StringComparison.OrdinalIgnoreCase))
            {
                return new AppDistributionInfo(
                    AppDistributionChannel.Direct,
                    IsPackaged: false,
                    PackageFamilyName: string.Empty,
                    PackageFullName: string.Empty,
                    DetectionSource: "env:VAULTSYNC_DISTRIBUTION_CHANNEL");
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return new AppDistributionInfo(
                AppDistributionChannel.Direct,
                IsPackaged: false,
                PackageFamilyName: string.Empty,
                PackageFullName: string.Empty,
                DetectionSource: "non-windows");
        }

        var packageFamilyName = TryGetCurrentPackageFamilyName();
        var packageFullName = TryGetCurrentPackageFullName();
        var isPackaged = !string.IsNullOrWhiteSpace(packageFamilyName) || !string.IsNullOrWhiteSpace(packageFullName);

        if (string.Equals(packageFamilyName, StorePackageFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            return new AppDistributionInfo(
                AppDistributionChannel.Store,
                IsPackaged: true,
                PackageFamilyName: packageFamilyName,
                PackageFullName: packageFullName,
                DetectionSource: "package-family-name");
        }

        return new AppDistributionInfo(
            AppDistributionChannel.Direct,
            IsPackaged: isPackaged,
            PackageFamilyName: packageFamilyName,
            PackageFullName: packageFullName,
            DetectionSource: isPackaged ? "packaged-non-store" : "unpackaged");
    }

    private static string TryGetCurrentPackageFamilyName()
    {
        try
        {
            var length = 0u;
            var rc = GetCurrentPackageFamilyName(ref length, null);
            if (rc == AppModelErrorNoPackage)
                return string.Empty;

            if (length == 0)
                return string.Empty;

            var buffer = new char[length];
            rc = GetCurrentPackageFamilyName(ref length, buffer);
            if (rc != 0)
                return string.Empty;

            return new string(buffer).TrimEnd('\0');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryGetCurrentPackageFullName()
    {
        try
        {
            var length = 0u;
            var rc = GetCurrentPackageFullName(ref length, null);
            if (rc == AppModelErrorNoPackage)
                return string.Empty;

            if (length == 0)
                return string.Empty;

            var buffer = new char[length];
            rc = GetCurrentPackageFullName(ref length, buffer);
            if (rc != 0)
                return string.Empty;

            return new string(buffer).TrimEnd('\0');
        }
        catch
        {
            return string.Empty;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, char[]? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
