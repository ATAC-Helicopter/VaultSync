using VaultSync.Core.Services;

namespace VaultSync.UI.Infrastructure;

public static class UiFormat
{
    public static string FormatBytes(long bytes, string numberFormat = "0.##") =>
        ByteSizeFormat.FormatBytes(bytes, numberFormat);

    public static string FormatSignedBytes(long bytes, string numberFormat = "0.##") =>
        ByteSizeFormat.FormatSignedBytes(bytes, numberFormat);
}
