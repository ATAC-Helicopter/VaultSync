using System.Globalization;
using System.Text;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Physical VT1 capture intent; no caller-selected path, descriptor, or executable.</summary>
internal static class LinuxRecoveryCaptureConsole
{
    internal static Task<string?> SelectAsync(Guid operation, LinuxBlockInventory inventory,
        Func<string, string?> text, CultureInfo culture, CancellationToken token) =>
        SelectAsync(operation, inventory, text, culture, LinuxRecoveryLocalConsole.OpenPhysical, token);

    internal static Task<string?> SelectAsync(Guid operation, LinuxBlockInventory inventory,
        Func<string, string?> text, CultureInfo culture, Func<LinuxRecoveryLocalConsole.IAccess> open,
        CancellationToken token) => Task.Run(() =>
    {
        if (operation == Guid.Empty) throw Invalid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var cancellation = deadline.Token;
        cancellation.ThrowIfCancellationRequested();
        var candidates = inventory.Devices.Where(Candidate).Take(LinuxBlockInventoryService.MaximumDevices).ToArray();
        if (candidates.Length == 0)
        {
            using var empty = open();
            empty.WaitForActive(cancellation); empty.ValidateContext();
            empty.Write(Encoding.UTF8.GetBytes(Format(text, culture, "NoSources") + "\n"), cancellation);
            return null;
        }
        var prompt = new StringBuilder();
        Line("Title"); Line("Warning");
        foreach (var source in candidates)
        {
            Line("Source", source.KernelName, source.Capacity.ToString("N0", culture),
                source.LogicalSectorBytes.ToString("N0", culture), source.PhysicalSectorBytes.ToString("N0", culture));
            Line("Challenge", Challenge(source.KernelName, operation));
        }
        byte[] bytes = Encoding.UTF8.GetBytes(prompt.ToString());
        if (bytes.Length > 65536) throw Invalid();
        using var access = open();
        access.WaitForActive(cancellation); access.ValidateContext(); access.FlushInput(); access.ValidateContext();
        access.Write(bytes, cancellation);
        var entered = new StringBuilder(128);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int value = access.ReadByte(cancellation);
            if (value == '\n') break;
            if (value is < 32 or > 126 || entered.Length == 128) throw Invalid();
            entered.Append((char)value);
        }
        cancellation.ThrowIfCancellationRequested(); access.ValidateContext();
        if (access.HasPendingInput()) throw Invalid();
        if (entered.Length == 0)
        {
            access.Write(Encoding.UTF8.GetBytes(Format(text, culture, "Cancelled") + "\n"), cancellation);
            return null;
        }
        var selected = candidates.Where(value => entered.ToString() == Challenge(value.KernelName, operation)).Take(2).ToArray();
        if (selected.Length != 1) throw Invalid();
        return selected[0].KernelName;

        void Line(string key, params object[] values) => prompt.Append(Format(text, culture, key, values)).Append('\n');
    }, CancellationToken.None);

    internal static bool Candidate(LinuxBlockObservation source) => source.Kind == LinuxBlockKind.WholeDisk &&
        source.ParentKernelName is null && source.Identity == LinuxBlockIdentity.UniqueInSnapshot && source.IsReadOnly &&
        source.Capacity is > 0 and <= LinuxBlockReadService.MaximumCapacity && source.ProfileGeometrySupported &&
        source.DiskSequence is > 0 && !source.MountObserved && !source.SwapObserved && !source.HasHolders &&
        source.Use == LinuxBlockUse.Unknown && source.KernelName is { Length: > 0 and <= 63 } name &&
        name.All(value => char.IsAsciiLetterOrDigit(value) || value is '_' or '-');

    internal static string Format(Func<string, string?> text, CultureInfo culture, string key, params object[] values)
    {
        string? template = text("Recovery.Capture." + key);
        if (template is not { Length: > 0 and <= 1024 } || template.Any(char.IsControl)) throw Invalid();
        var seen = new HashSet<int>();
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] == '}') throw Invalid();
            if (template[i] != '{') continue;
            if (i + 2 >= template.Length || template[i + 2] != '}' || template[i + 1] is < '0' or > '9') throw Invalid();
            int index = template[i + 1] - '0';
            if (index >= values.Length) throw Invalid();
            seen.Add(index); i += 2;
        }
        if (seen.Count != values.Length) throw Invalid();
        string result = string.Format(culture, template, values);
        if (result.Length > 4096 || result.Any(char.IsControl)) throw Invalid();
        return result;
    }

    private static string Challenge(string source, Guid operation) => source + " " + operation.ToString("D", CultureInfo.InvariantCulture);
    private static InvalidDataException Invalid() => new("recovery-capture.console-refused");
}
