using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VaultSync.Core.Models;

public enum DiagnosticOperation { Application, UserInterface, BackgroundTask, Snapshot, Backup, Verification, Restore, Update, DiskInventory }
public enum DiagnosticOutcome { Started, Completed, Failed, Cancelled, Crash, Hang }
public enum DiagnosticPhase { None, Initialize, Enumerate, Read, Compress, Write, Verify, Commit, Cleanup, Download, Apply }
public enum DiagnosticMode { None, Folder, Zip, Disk }
public enum DiagnosticFailure { None, Cancelled, AccessDenied, NotFound, StorageFull, Io, Network, Timeout, InvalidData, InvalidOperation, Argument, Cryptography, Database, OutOfMemory, Other, IntegrityMismatch }
public enum DiagnosticModule { Core, Ui, Cli }
public sealed record DiagnosticCodeSite(DiagnosticModule Module, int Token);

/// <summary>Only product codes, coarse measurements and build metadata. Never user strings or identifiers.</summary>
public sealed record ProductDiagnosticReport(int SchemaVersion, int ConsentVersion, Guid ReportId,
    string AppVersion, string RuntimeVersion, string? SourceCommit, string ReleaseChannel, string Os, int OsMajor, string Architecture,
    DiagnosticOperation Operation, DiagnosticOutcome Outcome, DiagnosticPhase Phase, DiagnosticMode Mode,
    DiagnosticFailure Failure, int DurationBucketMs, int QuantityBucket, IReadOnlyList<DiagnosticCodeSite> CodeSites)
{
    private static readonly int[] DurationBuckets = [0, 100, 1000, 5000, 15000, 60000, 300000, 1800000, 3600000, 86400000];
    private static readonly int[] QuantityBuckets = [0, 1, 10, 100, 1000, 10000, 100000, 1000000, int.MaxValue];

    public static ProductDiagnosticReport Create(DiagnosticOperation operation, DiagnosticOutcome outcome,
        Exception? exception = null, DiagnosticPhase phase = DiagnosticPhase.None, DiagnosticMode mode = DiagnosticMode.None,
        TimeSpan duration = default, long quantity = 0, Assembly? productAssembly = null)
    {
        Assembly product = productAssembly ?? typeof(ProductDiagnosticReport).Assembly;
        string? commit = product.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "VaultSyncSourceCommit")?.Value;
        if (commit is null || commit.Length is not (40 or 64) || commit.Any(c => !char.IsAsciiHexDigit(c))) commit = null;
        string channel = Channel(product);
        return new(1, channel == "beta" ? 2 : 1, Guid.NewGuid(), product.GetName().Version?.ToString() ?? "0.0.0",
            Environment.Version.ToString(), commit?.ToLowerInvariant(),
            channel,
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other",
            Math.Clamp(Environment.OSVersion.Version.Major, 0, 999),
            RuntimeInformation.ProcessArchitecture switch { System.Runtime.InteropServices.Architecture.X64 => "x64", System.Runtime.InteropServices.Architecture.Arm64 => "arm64", System.Runtime.InteropServices.Architecture.X86 => "x86", System.Runtime.InteropServices.Architecture.Arm => "arm", _ => "other" },
            operation, outcome, phase, mode, Classify(exception),
            Bucket(duration.TotalMilliseconds, DurationBuckets), Bucket(quantity, QuantityBuckets), Sites(exception));
    }

    internal static string Channel(Assembly assembly)
    {
        string? channel = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "VaultSyncReleaseChannel")?.Value;
        return channel is "development" or "candidate" or "beta" or "stable" ? channel : "development";
    }

    private static int Bucket(double value, int[] buckets) => buckets.FirstOrDefault(b => value <= b, buckets[^1]);

    private static DiagnosticFailure Classify(Exception? ex) => ex switch
    {
        null => DiagnosticFailure.None,
        OperationCanceledException => DiagnosticFailure.Cancelled,
        UnauthorizedAccessException => DiagnosticFailure.AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => DiagnosticFailure.NotFound,
        IOException io when (io.HResult & 0xffff) is 28 or 39 or 112 => DiagnosticFailure.StorageFull,
        InvalidDataException => DiagnosticFailure.InvalidData,
        IOException => DiagnosticFailure.Io,
        HttpRequestException or System.Net.Sockets.SocketException => DiagnosticFailure.Network,
        TimeoutException => DiagnosticFailure.Timeout,
        InvalidOperationException => DiagnosticFailure.InvalidOperation,
        ArgumentException => DiagnosticFailure.Argument,
        System.Security.Cryptography.CryptographicException => DiagnosticFailure.Cryptography,
        System.Data.Common.DbException => DiagnosticFailure.Database,
        OutOfMemoryException => DiagnosticFailure.OutOfMemory,
        _ => DiagnosticFailure.Other
    };

    private static IReadOnlyList<DiagnosticCodeSite> Sites(Exception? exception)
    {
        if (exception is null) return Array.Empty<DiagnosticCodeSite>();
        try
        {
            return new StackTrace(exception, false).GetFrames().Take(80)
                .Select(f => f.GetMethod()).OfType<MethodBase>()
                .Where(m => m.Module.Assembly.GetName().Name is "VaultSync.Core" or "VaultSync.UI" or "VaultSync.CLI")
                .Take(8).Select(m => new DiagnosticCodeSite(m.Module.Assembly.GetName().Name switch
                { "VaultSync.UI" => DiagnosticModule.Ui, "VaultSync.CLI" => DiagnosticModule.Cli, _ => DiagnosticModule.Core }, m.MetadataToken))
                .Where(s => s.Token is >= 0x06000001 and <= 0x06ffffff).ToArray();
        }
        catch { return Array.Empty<DiagnosticCodeSite>(); }
    }

    internal bool IsValid() => SchemaVersion == 1 &&
        (ConsentVersion == 1 || ConsentVersion == 2 && ReleaseChannel == "beta") && ReportId != Guid.Empty && ReportId.ToString("D")[14] == '4' && "89ab".Contains(ReportId.ToString("D")[19]) &&
        ValidVersion(AppVersion) && ValidVersion(RuntimeVersion) &&
        (SourceCommit is null || SourceCommit.Length is 40 or 64 && SourceCommit.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) &&
        ReleaseChannel is "development" or "candidate" or "beta" or "stable" &&
        Os is "windows" or "macos" or "linux" or "other" && OsMajor is >= 0 and <= 999 &&
        Architecture is "x64" or "arm64" or "x86" or "arm" or "other" &&
        Enum.IsDefined(Operation) && Enum.IsDefined(Outcome) && Enum.IsDefined(Phase) && Enum.IsDefined(Mode) && Enum.IsDefined(Failure) &&
        DurationBuckets.Contains(DurationBucketMs) && QuantityBuckets.Contains(QuantityBucket) &&
        CodeSites is { Count: <= 8 } && CodeSites.All(s => s is not null && Enum.IsDefined(s.Module) && s.Token is >= 0x06000001 and <= 0x06ffffff);

    private static bool ValidVersion(string? value) => value is { Length: >= 3 and <= 24 } &&
        value.All(c => char.IsAsciiDigit(c) || c == '.') && Version.TryParse(value, out _);
}
