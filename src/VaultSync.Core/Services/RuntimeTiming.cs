using System;
using System.Diagnostics;
using System.Globalization;

namespace VaultSync.Core.Services;

public static class RuntimeTiming
{
    public static RuntimeTimingScope Measure(string label) => new(label);
}

public readonly struct RuntimeTimingScope : IDisposable
{
    private readonly string _label;
    private readonly long _startTimestamp;
    private readonly bool _enabled;

    internal RuntimeTimingScope(string label)
    {
        _enabled = RuntimeLog.ShouldEmitVerbose && !string.IsNullOrWhiteSpace(label);
        _label = _enabled ? label : string.Empty;
        _startTimestamp = _enabled ? Stopwatch.GetTimestamp() : 0L;
    }

    public void Dispose()
    {
        if (!_enabled)
        {
            return;
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - _startTimestamp;
        double elapsedMs = elapsedTicks * 1000d / Stopwatch.Frequency;
        RuntimeLog.WriteVerbose(string.Create(
            CultureInfo.InvariantCulture,
            $"[Timing] {_label} finished in {elapsedMs:0.0} ms."));
    }
}
