using System;
using System.Diagnostics;
using System.IO;

namespace VaultSync.UI.Infrastructure;

internal static class SystemFileLauncher
{
    public static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        string fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveExecutablePath("explorer.exe"),
                UseShellExecute = false
            };
            psi.ArgumentList.Add(fullPath);
            Process.Start(psi);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            StartWithSingleArgument(ResolveExecutablePath("open"), fullPath);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            StartWithSingleArgument(ResolveExecutablePath("xdg-open"), fullPath);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = true
        });
    }

    public static void OpenUri(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException("Target URI is invalid.", nameof(target));

        if (!IsAllowedExternalScheme(uri.Scheme))
            throw new InvalidOperationException($"URI scheme '{uri.Scheme}' is not supported.");

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }

    private static void StartWithSingleArgument(string executable, string argument)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(argument);
        Process.Start(psi);
    }

    private static string ResolveExecutablePath(string executableName)
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (pathValue ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, executableName));
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore malformed PATH entries and continue to the next absolute candidate.
            }
        }

        throw new FileNotFoundException($"Required system launcher '{executableName}' was not found on PATH.");
    }

    internal static bool IsAllowedExternalScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "ms-windows-store", StringComparison.OrdinalIgnoreCase);
}
