using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VaultSync.Core.Services;

namespace VaultSync.Benchmarks;

internal static class CompressionProfile
{
    public static void Run(string[] args)
    {
        int outputIndex = Array.IndexOf(args, "--output");
        if (outputIndex < 0 || outputIndex + 1 >= args.Length)
            throw new ArgumentException("Compression profile requires --output PATH.");

        var yaml = new StringBuilder();
        for (int i = 0; i < 12000; i++)
            yaml.Append($"--- !u!114 &{i}\nMonoBehaviour:\n  m_Name: Vehicle{i}\n  m_Enabled: 1\n  speed: {i % 200}\n");
        byte[] unity = Encoding.UTF8.GetBytes(yaml.ToString());
        var random = new Random(1996);
        byte[] binary = new byte[1024 * 1024];
        random.NextBytes(binary);
        var fixtures = new Dictionary<string, Dictionary<string, byte[]>>
        {
            ["unity-text"] = new() { ["scene.unity"] = unity, ["vehicle.prefab"] = unity },
            ["unity-small-meta"] = Enumerable.Range(0, 2000).ToDictionary(
                i => $"Assets/vehicle{i}.meta", i => Encoding.UTF8.GetBytes(
                    $"fileFormatVersion: 2\nguid: {i:x32}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {{instanceID: 0}}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n")),
            ["binary-asset-proxy"] = new() { ["generated.asset"] = binary },
            ["already-compressed-proxy"] = new() { ["generated.zip"] = Zip(new() { ["data"] = binary }, _ => CompressionLevel.Optimal) }
        };
        var rows = new List<object>();
        foreach (var (fixture, files) in fixtures)
        {
            foreach (var level in new[] { "policy", "Fastest", "Optimal", "SmallestSize" })
            {
                Func<string, CompressionLevel> resolve = level == "policy"
                    ? BackupService.ResolveArchiveCompressionLevel
                    : _ => Enum.Parse<CompressionLevel>(level);
                var encodeTimes = new List<double>();
                var decodeTimes = new List<double>();
                long allocated = 0;
                byte[] stored = [];
                for (int iteration = 0; iteration < 6; iteration++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    stored = Zip(files, resolve);
                    double encodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
                    start = Stopwatch.GetTimestamp();
                    Verify(files, stored);
                    double decodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (iteration == 0) continue;
                    encodeTimes.Add(encodeMs);
                    decodeTimes.Add(decodeMs);
                    allocated += allocation;
                }
                rows.Add(new { fixture, level, logicalBytes = files.Values.Sum(b => (long)b.Length),
                    storedBytes = stored.Length, files = files.Count, encodeMedianMs = Median(encodeTimes),
                    encodeP95Ms = encodeTimes.Max(), decodeMedianMs = Median(decodeTimes),
                    managedEncodeAllocatedBytesMean = allocated / 5, roundtrip = true });
            }
        }
        var report = new { schemaVersion = 1, recordedUtc = DateTime.UtcNow,
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
            configuration = "Release", iterations = 5,
            worktreeBaseCommit = Git("rev-parse", "HEAD").Trim(),
            worktreeDirty = !string.IsNullOrWhiteSpace(Git("status", "--porcelain")),
            policySourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes("src/VaultSync.Core/Services/BackupService.cs"))),
            benchmarkSourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes("benchmarks/VaultSync.Benchmarks/CompressionProfile.cs"))),
            measurements = rows, limitations = new[] { "Generated fixtures; no real Unity project or network.",
                "Managed allocations exclude native compressor memory; all ZIP bytes are buffered in memory.",
                "Archive metadata included; deterministic timestamps. P95 is maximum of five samples.",
                "Release CLI harness; developer research, not a shipping CLI feature." } };
        string output = Path.GetFullPath(args[outputIndex + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Compression profile: {rows.Count} verified measurements; {output}");
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    private static string Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot read source identity.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Cannot read source identity.");
        return output;
    }

    private static byte[] Zip(Dictionary<string, byte[]> files, Func<string, CompressionLevel> resolve)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data) in files)
            {
                var entry = archive.CreateEntry(path, resolve(path));
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(data);
            }
        return output.ToArray();
    }

    private static void Verify(Dictionary<string, byte[]> files, byte[] stored)
    {
        using var input = new MemoryStream(stored);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count != files.Count) throw new InvalidDataException("Entry count mismatch.");
        foreach (var entry in archive.Entries)
        {
            using var decoded = new MemoryStream();
            using var stream = entry.Open();
            stream.CopyTo(decoded);
            if (!files.TryGetValue(entry.FullName, out byte[]? expected) || !decoded.ToArray().AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("ZIP roundtrip mismatch.");
        }
    }
}
