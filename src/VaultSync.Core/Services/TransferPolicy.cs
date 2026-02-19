using System;

namespace VaultSync.Core.Services;

/// <summary>
/// Shared transfer policy math for bandwidth throttling across native runners.
/// </summary>
public static class TransferPolicy
{
    public static int? NormalizeBandwidthLimitMbps(bool enabled, int configuredMbps)
    {
        if (!enabled)
            return null;

        return Math.Clamp(configuredMbps, 1, 5000);
    }

    /// <summary>
    /// Convert Mbps to rsync --bwlimit KB/s units.
    /// </summary>
    public static int ToRsyncBwLimitKbps(int maxBandwidthMbps)
    {
        var mbps = Math.Clamp(maxBandwidthMbps, 1, 5000);
        // 1 Mbps = 125 KB/s
        return mbps * 125;
    }

    /// <summary>
    /// Approximate robocopy /IPG milliseconds from a target Mbps and thread count.
    /// /IPG is a per-packet inter-gap, so this is best-effort, not exact shaping.
    /// </summary>
    public static int ToRobocopyIpgMilliseconds(int maxBandwidthMbps, int threadCount)
    {
        var mbps = Math.Clamp(maxBandwidthMbps, 1, 5000);
        var threads = Math.Clamp(threadCount, 1, 128);

        var bytesPerSecond = (mbps * 1024d * 1024d) / 8d;
        var bytesPerSecondPerThread = bytesPerSecond / threads;
        var bytesPerMillisecondPerThread = bytesPerSecondPerThread / 1000d;
        if (bytesPerMillisecondPerThread <= 0)
            return 0;

        // Robocopy chunks are roughly packet-sized; 64 KiB is a practical approximation.
        const double packetBytes = 64d * 1024d;
        var delayMs = (packetBytes / bytesPerMillisecondPerThread) - 1d;
        if (delayMs <= 0)
            return 0;

        return Math.Clamp((int)Math.Round(delayMs), 0, 5000);
    }
}

