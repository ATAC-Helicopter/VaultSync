using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace VaultSync.UI.Infrastructure;

/// <summary>
/// Builds the complete, user-visible crash report that may be shared with FGLabs.
/// This class deliberately uses an allowlist: it never consumes exception messages,
/// diagnostic logs, configuration, environment variables, paths, or user data.
/// </summary>
internal static partial class ShareableCrashReport
{
    internal const string SupportAddress = "crash-reports@fglabs.dev";
    private const int MaximumExceptionDepth = 8;
    private const int MaximumFramesPerException = 80;
    private const int MaximumSavedReports = 10;
    private static readonly TimeSpan SavedReportRetention = TimeSpan.FromDays(7);

    public static CrashReportDocument Create(
        Exception exception,
        string source,
        bool isTerminating,
        string applicationVersion)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string crashCategory = NormalizeSource(source);
        string crashReason = NormalizeTypeName(exception.GetType());
        string operatingSystemFamily = GetOperatingSystemFamily();
        string reportId = CreateReportId(crashCategory);
        string content = BuildContent(
            reportId,
            exception,
            crashCategory,
            crashReason,
            isTerminating,
            NormalizeVersion(applicationVersion),
            operatingSystemFamily);

        return new CrashReportDocument(
            reportId,
            operatingSystemFamily,
            crashCategory,
            crashReason,
            content);
    }

    public static string Save(
        CrashReportDocument report,
        string? managedDirectoryOverride = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        string directory = managedDirectoryOverride ?? GetShareableReportDirectory();
        Directory.CreateDirectory(directory);
        PruneSavedReports(directory);

        string path = Path.Combine(directory, $"vaultsync-crash-{report.ReportId}.txt");
        WritePrivateTextFile(path, report.Content);
        return path;
    }

    public static string BuildEmailSubject(CrashReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return $"[VaultSync crash {report.ReportId}]";
    }

    public static string BuildEmailBody() =>
            "I reviewed the attached redacted VaultSync crash report.\r\n\r\n" +
            "Nothing was sent automatically. This draft was prepared locally by VaultSync.\r\n" +
            "The redacted report is already attached. Review it before sending.\r\n\r\n" +
            "Optional description of what happened:\r\n";

    public static bool DeleteSavedReport(string? path, string? managedDirectoryOverride = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            string reportDirectory = Path.GetFullPath(managedDirectoryOverride ?? GetShareableReportDirectory());
            string candidate = Path.GetFullPath(path);
            string relative = Path.GetRelativePath(reportDirectory, candidate);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return false;

            File.Delete(candidate);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildContent(
        string reportId,
        Exception exception,
        string source,
        string crashReason,
        bool isTerminating,
        string applicationVersion,
        string operatingSystemFamily)
    {
        var builder = new StringBuilder();
        builder.AppendLine("VAULTSYNC / CRASH REPORT");
        builder.AppendLine("Generated and redacted locally. Nothing was sent automatically.");
        builder.AppendLine();
        builder.AppendLine("REPORT IDENTITY");
        builder.AppendLine("---------------");
        builder.AppendLine($"Report ID: {reportId}");
        const string applicationVersionToken = "<VAULTSYNC-APPLICATION-VERSION>";
        builder.AppendLine($"Application version: {applicationVersionToken}");
        builder.AppendLine($"Operating system family: {operatingSystemFamily}");
        builder.AppendLine($"Crash category: {source}");
        builder.AppendLine($"Crash reason: {crashReason}");
        builder.AppendLine($"Application must close: {(isTerminating ? "yes" : "no")}");
        builder.AppendLine();
        builder.AppendLine("CRASH DETAILS");
        builder.AppendLine("-------------");

        Exception? current = exception;
        for (int depth = 0; current is not null && depth < MaximumExceptionDepth; depth++)
        {
            builder.AppendLine($"Exception {depth + 1}: {NormalizeTypeName(current.GetType())}");
            IReadOnlyList<string> frames = GetSafeFrames(current);
            if (frames.Count == 0)
            {
                builder.AppendLine("  (no application call sites available)");
            }
            else
            {
                foreach (string frame in frames)
                    builder.AppendLine($"  at {frame}");
            }

            current = current.InnerException;
        }

        if (current is not null)
            builder.AppendLine("Additional nested exceptions were omitted.");

        builder.AppendLine();
        builder.AppendLine("PRIVACY BOUNDARY");
        builder.AppendLine("----------------");
        builder.AppendLine("Intentionally excluded:");
        builder.AppendLine("- Exception messages and file contents");
        builder.AppendLine("- User, machine, project, backup, snapshot, and destination names");
        builder.AppendLine("- File, folder, database, log, command-line, and network paths");
        builder.AppendLine("- Credentials, identifiers, addresses, environment variables, and configuration");
        builder.AppendLine("- OS version, locale, architecture, process details, timestamps, and raw diagnostics");
        builder.AppendLine();
        builder.AppendLine("The generated report above is read-only in VaultSync.");

        return SanitizeDefenseInDepth(builder.ToString())
            .Replace(applicationVersionToken, applicationVersion, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> GetSafeFrames(Exception exception)
    {
        var safeFrames = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (StackFrame frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            Type? declaringType = method?.DeclaringType;
            string? namespaceName = declaringType?.Namespace;
            if (method is null || declaringType is null || namespaceName is null ||
                !namespaceName.StartsWith("VaultSync", StringComparison.Ordinal))
            {
                continue;
            }

            (Type displayType, string displayMethod) = ResolveDisplayMethod(declaringType, method.Name);
            string typeName = NormalizeTypeName(displayType);
            string methodName = NormalizeMethodName(displayMethod);
            string value = $"{typeName}.{methodName}()";
            if (seen.Add(value))
                safeFrames.Add(value);

            if (safeFrames.Count >= MaximumFramesPerException)
                break;
        }

        return safeFrames;
    }

    private static string NormalizeTypeName(Type type) => NormalizeTypeName(type.FullName ?? type.Name);

    private static string NormalizeTypeName(string value)
    {
        string withoutGenericArity = GenericArityRegex().Replace(value, string.Empty);
        return SafeIdentifierRegex().IsMatch(withoutGenericArity)
            ? withoutGenericArity
            : "VaultSync.UnknownException";
    }

    private static string NormalizeMethodName(string value)
    {
        string normalized = AsyncStateMachineRegex().Replace(value, "$1");
        return SafeMethodRegex().IsMatch(normalized) ? normalized : "UnknownMethod";
    }

    private static (Type Type, string Method) ResolveDisplayMethod(Type declaringType, string methodName)
    {
        Match stateMachine = AsyncStateMachineTypeRegex().Match(declaringType.Name);
        if (stateMachine.Success && declaringType.DeclaringType is not null)
            return (declaringType.DeclaringType, stateMachine.Groups["method"].Value);

        return (declaringType, methodName);
    }

    private static string NormalizeSource(string source) => source switch
    {
        "AppDomain" => "application-domain",
        "UI thread" => "user-interface",
        "UnobservedTaskException" => "background-task",
        _ => "application"
    };

    private static string CreateReportId(string crashCategory)
    {
        string categoryCode = crashCategory switch
        {
            "user-interface" => "UI",
            "application-domain" => "APP",
            "background-task" => "TASK",
            _ => "GEN"
        };

        return $"CRASH-{categoryCode}-{Guid.NewGuid():N}".ToUpperInvariant();
    }

    private static string NormalizeVersion(string version)
    {
        Match match = VersionRegex().Match(version ?? string.Empty);
        return match.Success ? match.Value : "unknown";
    }

    private static string GetOperatingSystemFamily()
    {
        if (OperatingSystem.IsWindows())
            return "Windows";
        if (OperatingSystem.IsMacOS())
            return "macOS";
        if (OperatingSystem.IsLinux())
            return "Linux";
        return "Other";
    }

    private static string SanitizeDefenseInDepth(string value)
    {
        string sanitized = EmailRegex().Replace(value, "<redacted-email>");
        sanitized = UrlRegex().Replace(sanitized, "<redacted-url>");
        sanitized = WindowsPathRegex().Replace(sanitized, "<redacted-path>");
        sanitized = UnixPathRegex().Replace(sanitized, "<redacted-path>");
        sanitized = IpAddressRegex().Replace(sanitized, "<redacted-address>");
        sanitized = GuidRegex().Replace(sanitized, "<redacted-identifier>");
        return sanitized;
    }

    private static string GetShareableReportDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VaultSync",
        "crash",
        "shareable");

    private static void PruneSavedReports(string directory)
    {
        try
        {
            DateTime cutoffUtc = DateTime.UtcNow - SavedReportRetention;
            FileInfo[] reports = new DirectoryInfo(directory)
                .EnumerateFiles("vaultsync-crash-*.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            foreach (FileInfo report in reports.Where((file, index) =>
                         index >= MaximumSavedReports - 1 || file.LastWriteTimeUtc < cutoffUtc))
            {
                try
                {
                    report.Delete();
                }
                catch
                {
                    // Retention is best effort and must never block crash handling.
                }
            }
        }
        catch
        {
            // Retention is best effort and must never block crash handling.
        }
    }

    private static void WritePrivateTextFile(string path, string content)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content, encoding);
            return;
        }

        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.Create,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            using var writer = new StreamWriter(stream, encoding);
            writer.Write(content);
        }
        catch (PlatformNotSupportedException)
        {
            File.WriteAllText(path, content, encoding);
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Some filesystems do not support Unix mode bits.
            }
        }
    }

    [GeneratedRegex(@"`\d+")]
    private static partial Regex GenericArityRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+(?:\+[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex SafeIdentifierRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_<>-]*$")]
    private static partial Regex SafeMethodRegex();

    [GeneratedRegex(@"^<([^>]+)>.*$")]
    private static partial Regex AsyncStateMachineRegex();

    [GeneratedRegex(@"^<(?<method>[^>]+)>d__\d+$")]
    private static partial Regex AsyncStateMachineTypeRegex();

    [GeneratedRegex(@"\b\d+\.\d+(?:\.\d+){0,2}\b")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:https?|ftp)://[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex("(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\r\\n\\t\\\"<>|]*")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])/(?:[^\s/:]+/)+[^\s:)]*")]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex IpAddressRegex();

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRegex();
}

internal sealed record CrashReportDocument(
    string ReportId,
    string OperatingSystemFamily,
    string CrashCategory,
    string CrashReason,
    string Content);
