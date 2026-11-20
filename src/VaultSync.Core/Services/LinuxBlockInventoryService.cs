using System.Globalization;
using System.Text;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Reads fixed Linux sysfs/proc metadata paths only. No device opens, mounts, subprocesses or writes.
/// Bracketed observations are not atomic, authenticated or a global proof that a device is offline.
/// </summary>
public static class LinuxBlockInventoryService
{
    public const int MaximumDevices = 256;
    public const int MaximumEdges = 4096;
    public const int MaximumMetadataBytes = 8 * 1024 * 1024;
    private const string BlockRoot = "/sys/class/block";
    private const string MountInfo = "/proc/self/mountinfo";
    private const string Swaps = "/proc/swaps";
    private const string BootId = "/proc/sys/kernel/random/boot_id";
    private const string MountNamespace = "/proc/self/ns/mnt";

    public static ValueTask<LinuxBlockInventory> ObserveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return OperatingSystem.IsLinux() ? ObserveAsync(new KernelFiles(), cancellationToken) :
            ValueTask.FromResult(Failure(LinuxBlockInventoryState.UnsupportedPlatform, LinuxBlockInventoryReason.LinuxRequired));
    }

    internal interface IFiles
    {
        ValueTask<string> ReadAsync(string path, int maximumBytes, CancellationToken token);
        IReadOnlyList<string> Names(string path, int maximumCount);
        string ResolveBlock(string path);
        string ReadLink(string path);
    }

    private sealed record Node(string Name, string Path, string Number, string Event, bool Partition, ulong Sectors,
        bool ReadOnly, uint Logical, uint Physical, ulong? Sequence, string[] Identities, string? Type,
        string[] Holders, string[] Slaves, ulong Start);

    internal static async ValueTask<LinuxBlockInventory> ObserveAsync(IFiles files, CancellationToken token = default)
    {
        var reader = new Reader(files, token);
        try
        {
            token.ThrowIfCancellationRequested();
            string[] names = reader.Names(BlockRoot);
            string bootText = await reader.Read(BootId, 64).ConfigureAwait(false);
            if (!Guid.TryParseExact(bootText, "D", out Guid boot) || boot == Guid.Empty)
                throw new InvalidDataException();
            string ns = files.ReadLink(MountNamespace);
            if (!ValidNamespace(ns))
                throw new InvalidDataException();
            string mounts = await reader.Read(MountInfo, 1024 * 1024).ConfigureAwait(false);
            string swaps = await reader.Read(Swaps, 1024 * 1024).ConfigureAwait(false);
            HashSet<string> mounted = ParseMounts(mounts);
            Dictionary<string, Node> nodes = await ReadNodes(reader, files, names, token).ConfigureAwait(false);
            var (parents, related) = Topology(nodes);
            (HashSet<string> swapNames, bool unresolvedSwaps) = ParseSwaps(swaps, nodes, files);
            HashSet<string> used = InUse(nodes, mounted, swapNames, related);
            if (!await Unchanged(reader, files, nodes, names, bootText, ns, mounts, swaps, token).ConfigureAwait(false))
                return Changed();
            var observations = Observations(nodes, parents, mounted, swapNames, used);
            token.ThrowIfCancellationRequested();
            return new(LinuxBlockInventoryState.Observed, LinuxBlockInventoryReason.NamespaceObservationOnly,
                Guid.NewGuid(), DateTimeOffset.UtcNow, boot, ns, unresolvedSwaps, observations);
        }
        catch (LimitException) { return Failure(LinuxBlockInventoryState.Incomplete, LinuxBlockInventoryReason.ResourceLimit); }
        catch (InvalidDataException) { return Failure(LinuxBlockInventoryState.Incomplete, LinuxBlockInventoryReason.InvalidMetadata); }
        catch (IOException) { return Failure(LinuxBlockInventoryState.Incomplete, LinuxBlockInventoryReason.UnreadableMetadata); }
        catch (UnauthorizedAccessException) { return Failure(LinuxBlockInventoryState.Incomplete, LinuxBlockInventoryReason.UnreadableMetadata); }
        catch (DecoderFallbackException) { return Failure(LinuxBlockInventoryState.Incomplete, LinuxBlockInventoryReason.InvalidMetadata); }
    }

    private static async ValueTask<Dictionary<string, Node>> ReadNodes(Reader reader, IFiles files, string[] names, CancellationToken token)
    {
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        int edges = 0;
        foreach (string name in names)
        {
            token.ThrowIfCancellationRequested();
            string path = BlockRoot + "/" + name;
            string resolved = files.ResolveBlock(path);
            if (!resolved.StartsWith("/sys/devices/", StringComparison.Ordinal) || !resolved.EndsWith("/" + name, StringComparison.Ordinal) ||
                resolved.Contains("/../", StringComparison.Ordinal))
                throw new InvalidDataException();
            string uevent = await reader.Read(path + "/uevent", 4096).ConfigureAwait(false);
            Dictionary<string, string> fields = ParseEvent(uevent);
            if (!fields.TryGetValue("DEVNAME", out string? devName) || devName != name ||
                !fields.TryGetValue("DEVTYPE", out string? type) || type is not ("disk" or "partition"))
                throw new InvalidDataException();
            string number = await reader.Read(path + "/dev", 32).ConfigureAwait(false);
            if (!ValidNumber(number) || !fields.TryGetValue("MAJOR", out var major) || !fields.TryGetValue("MINOR", out var minor) ||
                number != major + ":" + minor)
                throw new InvalidDataException();
            ulong sectors = Number(await reader.Read(path + "/size", 32).ConfigureAwait(false));
            if (sectors > ulong.MaxValue / 512)
                throw new InvalidDataException();
            string ro = await reader.Read(path + "/ro", 8).ConfigureAwait(false);
            if (ro is not ("0" or "1"))
                throw new InvalidDataException();
            bool partition = type == "partition";
            uint logical = 0, physical = 0;
            ulong? sequence = null;
            string[] identity = [];
            string? deviceType = null;
            ulong start = 0;
            if (partition)
            {
                if (Number(await reader.Read(path + "/partition", 32).ConfigureAwait(false)) == 0)
                    throw new InvalidDataException();
                start = Number(await reader.Read(path + "/start", 32).ConfigureAwait(false));
                if (start > ulong.MaxValue / 512)
                    throw new InvalidDataException();
            }
            else
            {
                logical = Sector(await reader.Read(path + "/queue/logical_block_size", 32).ConfigureAwait(false));
                physical = Sector(await reader.Read(path + "/queue/physical_block_size", 32).ConfigureAwait(false));
                if (physical < logical || sectors * 512 % logical != 0)
                    throw new InvalidDataException();
                string? seq = await reader.Optional(path + "/diskseq", 32).ConfigureAwait(false);
                if (seq is not null)
                {
                    sequence = Number(seq);
                    if (sequence == 0)
                        throw new InvalidDataException();
                }
                deviceType = await reader.Optional(path + "/device/type", 32).ConfigureAwait(false);
                if (deviceType is not null)
                    _ = Number(deviceType);
                if (!resolved.StartsWith("/sys/devices/virtual/", StringComparison.Ordinal) && deviceType is null or "0")
                    identity = await PersistentIdentities(reader, path).ConfigureAwait(false);
            }
            string[] holders = reader.Names(path + "/holders"), slaves = reader.Slaves(path + "/slaves", partition);
            edges += holders.Length + slaves.Length;
            if (edges > MaximumEdges)
                throw new LimitException();
            nodes.Add(name, new(name, resolved, number, uevent, partition, sectors, ro == "1", logical, physical,
                sequence, identity, deviceType, holders, slaves, start));
        }
        if (nodes.Values.Select(n => n.Number).Distinct(StringComparer.Ordinal).Count() != nodes.Count ||
            nodes.Values.Select(n => n.Path).Distinct(StringComparer.Ordinal).Count() != nodes.Count)
            throw new InvalidDataException();
        return nodes;
    }

    private static (Dictionary<string, Node>, Dictionary<string, HashSet<string>>) Topology(Dictionary<string, Node> nodes)
    {
        var related = nodes.Keys.ToDictionary(n => n, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var parents = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (Node node in nodes.Values)
        {
            if (node.Partition)
            {
                string parentPath = node.Path[..node.Path.LastIndexOf('/')];
                Node? parent = nodes.Values.SingleOrDefault(n => n.Path == parentPath);
                if (parent is null || parent.Partition || node.Start > parent.Sectors || node.Sectors > parent.Sectors - node.Start ||
                    node.Start * 512 % parent.Logical != 0 || node.Sectors * 512 % parent.Logical != 0)
                    throw new InvalidDataException();
                parents.Add(node.Name, parent);
                Connect(related, node.Name, parent.Name);
            }
            foreach (string holder in node.Holders)
            {
                if (!nodes.TryGetValue(holder, out Node? other) || !other.Slaves.Contains(node.Name, StringComparer.Ordinal))
                    throw new InvalidDataException();
                Connect(related, node.Name, holder);
            }
            foreach (string slave in node.Slaves)
            {
                if (!nodes.TryGetValue(slave, out Node? other) || !other.Holders.Contains(node.Name, StringComparer.Ordinal))
                    throw new InvalidDataException();
                Connect(related, node.Name, slave);
            }
        }
        return (parents, related);
    }

    private static HashSet<string> InUse(Dictionary<string, Node> nodes, HashSet<string> mounted,
        HashSet<string> swapNames, Dictionary<string, HashSet<string>> related)
    {
        var used = new HashSet<string>(nodes.Values.Where(n => mounted.Contains(n.Number) || swapNames.Contains(n.Name) || n.Holders.Length != 0)
            .Select(n => n.Name), StringComparer.Ordinal);
        var pending = new Queue<string>(used);
        while (pending.TryDequeue(out string? current))
            foreach (string neighbor in related[current])
            if (used.Add(neighbor))
                pending.Enqueue(neighbor);
        return used;
    }

    private static async ValueTask<bool> Unchanged(Reader reader, IFiles files, Dictionary<string, Node> nodes,
        string[] names, string bootText, string ns, string mounts, string swaps, CancellationToken token)
    {
        foreach (Node node in nodes.Values)
        {
            token.ThrowIfCancellationRequested();
            string path = BlockRoot + "/" + node.Name;
            if (files.ResolveBlock(path) != node.Path || await reader.Read(path + "/uevent", 4096).ConfigureAwait(false) != node.Event ||
                await reader.Read(path + "/dev", 32).ConfigureAwait(false) != node.Number ||
                Number(await reader.Read(path + "/size", 32).ConfigureAwait(false)) != node.Sectors ||
                await reader.Read(path + "/ro", 8).ConfigureAwait(false) != (node.ReadOnly ? "1" : "0") ||
                !reader.Names(path + "/holders").SequenceEqual(node.Holders) || !reader.Slaves(path + "/slaves", node.Partition).SequenceEqual(node.Slaves))
                return false;
            if (node.Partition)
            {
                if (Number(await reader.Read(path + "/start", 32).ConfigureAwait(false)) != node.Start ||
                    Number(await reader.Read(path + "/partition", 32).ConfigureAwait(false)) == 0)
                    return false;
            }
            else if (await reader.Optional(path + "/diskseq", 32).ConfigureAwait(false) != node.Sequence?.ToString(CultureInfo.InvariantCulture) ||
                Sector(await reader.Read(path + "/queue/logical_block_size", 32).ConfigureAwait(false)) != node.Logical ||
                Sector(await reader.Read(path + "/queue/physical_block_size", 32).ConfigureAwait(false)) != node.Physical ||
                await reader.Optional(path + "/device/type", 32).ConfigureAwait(false) != node.Type ||
                (Kind(node) == LinuxBlockKind.WholeDisk && !(await PersistentIdentities(reader, path).ConfigureAwait(false)).SequenceEqual(node.Identities)))
                return false;
        }
        if (!reader.Names(BlockRoot).SequenceEqual(names) || await reader.Read(BootId, 64).ConfigureAwait(false) != bootText ||
            files.ReadLink(MountNamespace) != ns || await reader.Read(MountInfo, 1024 * 1024).ConfigureAwait(false) != mounts ||
            await reader.Read(Swaps, 1024 * 1024).ConfigureAwait(false) != swaps)
            return false;
        return true;
    }

    private static IReadOnlyList<LinuxBlockObservation> Observations(Dictionary<string, Node> nodes,
        Dictionary<string, Node> parents, HashSet<string> mounted, HashSet<string> swapNames, HashSet<string> used)
    {
        var ambiguous = nodes.Values.Where(n => Kind(n) == LinuxBlockKind.WholeDisk)
            .SelectMany(n => n.Identities.Select(id => (Name: n.Name, Id: id)))
            .GroupBy(entry => entry.Id, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(entry => entry.Name)).ToHashSet(StringComparer.Ordinal);
        var observations = nodes.Values.OrderBy(n => n.Name, StringComparer.Ordinal).Select(n =>
        {
            Node disk = n.Partition ? parents[n.Name] : n;
            LinuxBlockKind kind = Kind(n);
            LinuxBlockIdentity identity = kind != LinuxBlockKind.WholeDisk || disk.Identities.Length == 0 ? LinuxBlockIdentity.Unknown :
                ambiguous.Contains(disk.Name) ? LinuxBlockIdentity.Ambiguous : LinuxBlockIdentity.UniqueInSnapshot;
            return new LinuxBlockObservation(n.Name, n.Number, n.Partition ? disk.Name : null, kind, n.Sectors * 512,
                disk.Logical, disk.Physical, disk.Sequence, n.ReadOnly, disk.Identities.FirstOrDefault(), identity,
                mounted.Contains(n.Number), swapNames.Contains(n.Name), n.Holders.Length != 0,
                used.Contains(n.Name) ? LinuxBlockUse.ObservedInUse : LinuxBlockUse.Unknown);
        }).ToList().AsReadOnly();
        return observations;
    }

    private static LinuxBlockKind Kind(Node n) => n.Partition ? LinuxBlockKind.Partition :
        n.Path.StartsWith("/sys/devices/virtual/", StringComparison.Ordinal) ? LinuxBlockKind.VirtualOrStacked :
        n.Type is not null and not "0" ? LinuxBlockKind.Other : LinuxBlockKind.WholeDisk;
    private static void Connect(Dictionary<string, HashSet<string>> graph, string a, string b)
    {
        if (a == b)
            throw new InvalidDataException();
        graph[a].Add(b);
        graph[b].Add(a);
    }
    private static LinuxBlockInventory Changed() => Failure(LinuxBlockInventoryState.ChangedDuringRead, LinuxBlockInventoryReason.MetadataChanged);
    private static LinuxBlockInventory Failure(LinuxBlockInventoryState state, LinuxBlockInventoryReason reason) =>
        new(state, reason, Guid.Empty, DateTimeOffset.UtcNow, Guid.Empty, null, false, Array.Empty<LinuxBlockObservation>());
    private static bool Name(string n) => n.Length is > 0 and <= 128 && n[0] != '.' &&
        n.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '!');
    private static ulong Number(string value) => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) ? number : throw new InvalidDataException();
    private static uint Sector(string text)
    {
        ulong value = Number(text);
        return value is >= 512 and <= 1048576 && (value & (value - 1)) == 0 ? (uint)value : throw new InvalidDataException();
    }
    private static bool ValidNumber(string value)
    {
        string[] parts = value.Split(':');
        return parts.Length == 2 && parts.All(p => uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out uint n) && p == n.ToString(CultureInfo.InvariantCulture));
    }
    private static bool ValidNamespace(string ns) => ns.StartsWith("mnt:[", StringComparison.Ordinal) && ns.EndsWith(']') && ns.Length is >= 7 and <= 32 &&
        ulong.TryParse(ns.AsSpan(5, ns.Length - 6), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    private static async ValueTask<string[]> PersistentIdentities(Reader reader, string path)
    {
        var result = new List<string>();
        foreach (var entry in new[] { ("/wwid", "wwid:"), ("/device/wwid", "wwid:"), ("/serial", "serial:"), ("/device/serial", "serial:") })
        {
            string? text = await reader.Optional(path + entry.Item1, 256).ConfigureAwait(false);
            if (text is null)
                continue;
            text = text.Trim(' ');
            if (text.Length == 0)
                continue;
            if (text.Any(char.IsControl))
                throw new InvalidDataException();
            string identity = entry.Item2 + text;
            if (!result.Contains(identity, StringComparer.Ordinal))
                result.Add(identity);
        }
        return result.ToArray();
    }
    private static Dictionary<string, string> ParseEvent(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 || !fields.TryAdd(line[..separator], line[(separator + 1)..]))
                throw new InvalidDataException();
        }
        return fields;
    }
    private static HashSet<string> ParseMounts(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<ulong>();
        string[] lines = text.Length == 0 ? [] : text.Split('\n');
        if (lines.Length is 0 or > 4096)
            throw new InvalidDataException();
        foreach (string line in lines)
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int separator = Array.IndexOf(fields, "-");
            if (fields.Length < 10 || separator < 6 || fields.Length != separator + 4 ||
                !ids.Add(Number(fields[0])) || !ValidNumber(fields[2]) || !fields[3].StartsWith('/') || !fields[4].StartsWith('/'))
                throw new InvalidDataException();
            _ = Number(fields[1]);
            result.Add(fields[2]);
        }
        return result;
    }
    private static (HashSet<string>, bool) ParseSwaps(string text, Dictionary<string, Node> nodes, IFiles files)
    {
        string[] lines = text.Split('\n');
        if (lines.Length > 1025)
            throw new LimitException();
        if (!lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).SequenceEqual(new[] { "Filename", "Type", "Size", "Used", "Priority" }))
            throw new InvalidDataException();
        var swaps = new HashSet<string>(StringComparer.Ordinal);
        bool unresolved = false;
        foreach (string line in lines.Skip(1))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5 || fields[1] is not ("partition" or "file") ||
                !long.TryParse(fields[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException();
            _ = Number(fields[2]);
            _ = Number(fields[3]);
            if (fields[1] == "file" || !fields[0].StartsWith("/dev/", StringComparison.Ordinal) || fields[0].Contains("..", StringComparison.Ordinal) || fields[0].Contains('\\'))
            {
                unresolved = true;
                continue;
            }
            string path;
            try
            {
                path = files.ReadLink(fields[0]);
            }
            catch (IOException) { unresolved = true; continue; }
            if (!path.StartsWith("/dev/", StringComparison.Ordinal))
            {
                unresolved = true;
                continue;
            }
            string name = path[5..];
            if (!Name(name) || !nodes.ContainsKey(name))
            {
                unresolved = true;
                continue;
            }
            swaps.Add(name);
        }
        return (swaps, unresolved);
    }

    private sealed class LimitException : IOException
    {
    }
    private sealed class Reader(IFiles files, CancellationToken token)
    {
        private int total;
        public async ValueTask<string> Read(string path, int limit)
        {
            token.ThrowIfCancellationRequested();
            string text = await files.ReadAsync(path, limit, token).ConfigureAwait(false);
            int size = Encoding.UTF8.GetByteCount(text);
            if (size > limit || size > MaximumMetadataBytes - total)
                throw new LimitException();
            total += size;
            return text.TrimEnd('\n');
        }
        public async ValueTask<string?> Optional(string path, int limit)
        {
            try
            {
                return await Read(path, limit).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        public string[] Slaves(string path, bool partition)
        {
            try
            {
                return Names(path);
            }
            // Partitions inherit lower-device relationships through their parent disk.
            catch (DirectoryNotFoundException) when (partition) { return []; }
        }
        public string[] Names(string path)
        {
            token.ThrowIfCancellationRequested();
            string[] names = files.Names(path, MaximumDevices).Order(StringComparer.Ordinal).ToArray();
            if (names.Length > MaximumDevices)
                throw new LimitException();
            if (names.Any(n => !Name(n)) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new InvalidDataException();
            return names;
        }
    }
    private sealed class KernelFiles : IFiles
    {
        public async ValueTask<string> ReadAsync(string path, int maximumBytes, CancellationToken token)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            byte[] bytes = new byte[maximumBytes + 1];
            int length = 0;
            while (length < bytes.Length)
            {
                int count = await file.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
                if (count == 0)
                    break;
                length += count;
            }
            if (length > maximumBytes)
                throw new LimitException();
            return new UTF8Encoding(false, true).GetString(bytes, 0, length);
        }
        public IReadOnlyList<string> Names(string path, int maximumCount) => Directory.EnumerateFileSystemEntries(path)
            .Take(maximumCount + 1).Select(Path.GetFileName).Select(n => n!).ToArray();
        public string ResolveBlock(string path) => new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? throw new IOException();
        public string ReadLink(string path) => path == MountNamespace ? new FileInfo(path).LinkTarget ?? throw new IOException() :
            new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
    }
}
