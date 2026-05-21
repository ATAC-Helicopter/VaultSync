using System;
using System.Globalization;

namespace VaultSync.Core.Services;

public static class ByteSizeFormat
{
    public static string FormatBytes(long bytes, string numberFormat = "0.##")
    {
        double size = bytes;
        string unit = "B";

        if (Math.Abs(size) >= 1024d)
        {
            size /= 1024d;
            unit = "KB";
        }

        if (Math.Abs(size) >= 1024d)
        {
            size /= 1024d;
            unit = "MB";
        }

        if (Math.Abs(size) >= 1024d)
        {
            size /= 1024d;
            unit = "GB";
        }

        if (Math.Abs(size) >= 1024d)
        {
            size /= 1024d;
            unit = "TB";
        }

        return $"{size.ToString(numberFormat, CultureInfo.CurrentCulture)} {unit}";
    }

    public static string FormatSignedBytes(long bytes, string numberFormat = "0.##")
    {
        string absolute = FormatBytes(Math.Abs(bytes), numberFormat);
        if (bytes > 0)
            return $"+{absolute}";
        if (bytes < 0)
            return $"-{absolute}";
        return absolute;
    }
}
