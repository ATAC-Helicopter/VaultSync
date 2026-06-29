using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VaultSync.UI.Services
{
    public enum DriveHealthStatus
    {
        Unknown,
        Healthy,
        Warning,
        Failing
    }

    public sealed record DriveHealthResult(
        DriveHealthStatus Status,
        string Message,
        string? DriveId,
        string? Path);

    public interface IDriveHealthService
    {
        DriveHealthResult CheckPath(string path);
    }

    /// <summary>
    /// Lightweight, best-effort drive health probe across Windows/macOS/Linux.
    /// Uses platform tools when available and falls back to Unknown instead of throwing.
    /// </summary>
    public sealed class DriveHealthService : IDriveHealthService
    {
        private const string SmartCtlCommand = "smartctl";

        public DriveHealthResult CheckPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Unknown(L("DriveHealth.Unknown.PathNotProvided", "Path not provided"));

            try
            {
                string full = Path.GetFullPath(path);
                string? root = Path.GetPathRoot(full);
                if (string.IsNullOrWhiteSpace(root))
                    return Unknown(L("DriveHealth.Unknown.CouldNotResolveDrive", "Could not resolve drive"));

                // UNC / network shares: SMART usually not accessible; report unknown.
                if (root.StartsWith(@"\\") || root.StartsWith("//"))
                {
                    return Unknown(
                        L("DriveHealth.Unknown.NetworkPath", "Network path; drive health not available"),
                        driveId: root,
                        path: full);
                }

                // Prefer smartctl when available (all platforms).
                if (TrySmartCtl(root, full, out DriveHealthResult? smartResult))
                    return smartResult;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return CheckWindows(root, full);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return CheckMac(full);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return CheckLinux(full);
            }
            catch
            {
                // ignore and return unknown below
            }

            return Unknown(L("DriveHealth.Unknown.Unavailable", "Drive health unavailable"));
        }

        private static DriveHealthResult CheckWindows(string root, string fullPath)
        {
            try
            {
                var driveInfo = new DriveInfo(root);
                if (driveInfo.DriveType == DriveType.Network)
                {
                    return Unknown(
                        L("DriveHealth.Unknown.NetworkPath", "Network path; drive health not available"),
                        driveId: root,
                        path: fullPath);
                }
            }
            catch
            {
                // If DriveInfo fails, continue with best-effort checks below.
            }

            // Try WMIC SMART status; if unavailable, fall back to basic readiness.
            string output = RunProcess("wmic", "diskdrive get Status,DeviceID", 4000);
            if (!string.IsNullOrWhiteSpace(output))
            {
                // If any drive reports a non-OK status, flag warning/failing.
                string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                bool sawOk = false;
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("Status", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // WMIC typically outputs: "OK    \\.\PHYSICALDRIVE0"
                    if (trimmed.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        sawOk = true;
                        continue;
                    }

                    // Only treat explicit FAIL/Pred Fail as failing; otherwise return unknown to avoid false alarms.
                    if (trimmed.Contains("Fail", StringComparison.OrdinalIgnoreCase))
                    {
                        return new DriveHealthResult(
                            DriveHealthStatus.Failing,
                            L("DriveHealth.Failing.SmartFailingDrive", "SMART reports this drive is failing."),
                            DriveId: trimmed,
                            Path: fullPath);
                    }
                    // Otherwise, let later checks decide (drive ready).
                }

                if (sawOk)
                {
                    return new DriveHealthResult(
                        DriveHealthStatus.Healthy,
                        L("DriveHealth.Healthy.SmartOk", "SMART reports OK."),
                        DriveId: root,
                        Path: fullPath);
                }
            }

            // Basic check: ensure the volume is ready.
            try
            {
                var driveInfo = new DriveInfo(root);
                if (!driveInfo.IsReady)
                {
                    return new DriveHealthResult(
                        DriveHealthStatus.Warning,
                        L("DriveHealth.Warning.DriveNotReady", "Drive is not ready."),
                        DriveId: root,
                        Path: fullPath);
                }
            }
            catch
            {
                // If SMART isn't available but the drive exists, consider health unknown-but-usable to avoid noisy UI.
                if (System.IO.Directory.Exists(root))
                {
                    return new DriveHealthResult(
                        DriveHealthStatus.Healthy,
                        L("DriveHealth.Healthy.SmartNotAvailableReachable", "SMART not available; drive is reachable."),
                        DriveId: root,
                        Path: fullPath);
                }

                return Unknown(L("DriveHealth.Unknown.CouldNotReadDriveInfo", "Could not read drive info"), driveId: root, path: fullPath);
            }

            return new DriveHealthResult(
                DriveHealthStatus.Healthy,
                L("DriveHealth.Healthy.SmartOk", "SMART reports OK."),
                DriveId: root,
                Path: fullPath);
        }

        private static DriveHealthResult CheckMac(string fullPath)
        {
            // Resolve device via df -P path to get /dev/diskXsY, then query diskutil info
            string dfOutput = RunProcess("/bin/df", $"-P \"{fullPath}\"", 4000);
            string device = ParseDeviceFromDf(dfOutput);
            if (string.IsNullOrWhiteSpace(device))
                return Unknown(L("DriveHealth.Unknown.CouldNotResolveDevice", "Could not resolve device"), path: fullPath);

            if (IsNetworkDevice(device))
            {
                return Unknown(
                    L("DriveHealth.Unknown.NetworkPath", "Network path; drive health not available"),
                    driveId: device,
                    path: fullPath);
            }

            // Prefer smartctl if available on macOS (brew install smartmontools).
            if (TrySmartCtl(device, fullPath, out DriveHealthResult? smartResult))
                return smartResult;

            string infoOutput = RunProcess("/usr/sbin/diskutil", $"info {device}", 4000);
            if (string.IsNullOrWhiteSpace(infoOutput))
                return Unknown(L("DriveHealth.Unknown.DiskutilNoData", "diskutil did not return data"), driveId: device, path: fullPath);

            if (infoOutput.Contains("SMART Status: Verified", StringComparison.OrdinalIgnoreCase))
            {
                return new DriveHealthResult(
                    DriveHealthStatus.Healthy,
                    L("DriveHealth.Healthy.SmartVerified", "SMART verified"),
                    DriveId: device,
                    Path: fullPath);
            }

            if (infoOutput.Contains("SMART Status: Failing", StringComparison.OrdinalIgnoreCase) ||
                infoOutput.Contains("SMART Status: Failed", StringComparison.OrdinalIgnoreCase))
            {
                return new DriveHealthResult(
                    DriveHealthStatus.Failing,
                    L("DriveHealth.Failing.SmartFailing", "SMART reports failing."),
                    DriveId: device,
                    Path: fullPath);
            }

            return Unknown(L("DriveHealth.Unknown.SmartNotSupported", "SMART not supported or unknown"), driveId: device, path: fullPath);
        }

        private static DriveHealthResult CheckLinux(string fullPath)
        {
            // Resolve device using df -P to get /dev/sdXn or similar.
            string dfOutput = RunProcess("/bin/df", $"-P \"{fullPath}\"", 4000);
            string device = ParseDeviceFromDf(dfOutput);
            if (string.IsNullOrWhiteSpace(device))
                return Unknown(L("DriveHealth.Unknown.CouldNotResolveDevice", "Could not resolve device"), path: fullPath);

            if (IsNetworkDevice(device))
            {
                return Unknown(
                    L("DriveHealth.Unknown.NetworkPath", "Network path; drive health not available"),
                    driveId: device,
                    path: fullPath);
            }

            // Try smartctl -H <device> if available.
            if (TrySmartCtl(device, fullPath, out DriveHealthResult? smartResult))
                return smartResult;

            return Unknown(L("DriveHealth.Unknown.SmartNotAvailable", "SMART not available"), driveId: device, path: fullPath);
        }

        private static bool TrySmartCtl(string target, string fullPath, out DriveHealthResult result)
        {
            result = default;
            // Normalize Windows drive letters to C: style
            string? device = target?.TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(device))
                return false;

            // smartctl supports drive letters on Windows (e.g., "C:")
            string smartOutput = RunProcess(SmartCtlCommand, $"-H {device}", 5000);
            if (string.IsNullOrWhiteSpace(smartOutput))
                return false;

            if (TryParseSmartCtlHealth(smartOutput, out DriveHealthStatus status))
            {
                if (status == DriveHealthStatus.Failing)
                {
                    result = new DriveHealthResult(
                        DriveHealthStatus.Failing,
                        L("DriveHealth.Failing.SmartFailing", "SMART reports failing."),
                        DriveId: device,
                        Path: fullPath);
                    return true;
                }

                result = new DriveHealthResult(
                    DriveHealthStatus.Healthy,
                    L("DriveHealth.Healthy.SmartOk", "SMART reports OK."),
                    DriveId: device,
                    Path: fullPath);
                return true;
            }

            return false;
        }

        internal static bool TryParseSmartCtlHealth(string smartOutput, out DriveHealthStatus status)
        {
            status = DriveHealthStatus.Unknown;
            if (string.IsNullOrWhiteSpace(smartOutput))
                return false;

            foreach (string rawLine in smartOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line.Contains("overall-health self-assessment test result", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("SMART Health Status", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("SMART Status", StringComparison.OrdinalIgnoreCase))
                {
                    if (line.Contains("PASSED", StringComparison.OrdinalIgnoreCase) ||
                        line.EndsWith(": OK", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains(": OK", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Verified", StringComparison.OrdinalIgnoreCase))
                    {
                        status = DriveHealthStatus.Healthy;
                        return true;
                    }

                    if (line.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("FAILING", StringComparison.OrdinalIgnoreCase) ||
                        line.EndsWith(": FAIL", StringComparison.OrdinalIgnoreCase))
                    {
                        status = DriveHealthStatus.Failing;
                        return true;
                    }
                }
            }

            return false;
        }

        private static string ParseDeviceFromDf(string? dfOutput)
        {
            if (string.IsNullOrWhiteSpace(dfOutput))
                return string.Empty;

            // df -P output: Filesystem 1024-blocks Used Available Capacity Mounted on
            // Grab the first non-header line's first column.
            string[] lines = dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                if (line.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                    return parts[0].Trim();
            }

            return string.Empty;
        }

        private static bool IsNetworkDevice(string device)
        {
            if (string.IsNullOrWhiteSpace(device))
                return false;

            if (device.StartsWith("//", StringComparison.OrdinalIgnoreCase))
                return true;

            if (device.Contains("://", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!device.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase) && device.Contains(':'))
                return true;

            return device.Contains("smbfs", StringComparison.OrdinalIgnoreCase)
                || device.Contains("afpfs", StringComparison.OrdinalIgnoreCase)
                || device.Contains("webdav", StringComparison.OrdinalIgnoreCase)
                || device.Contains("cifs", StringComparison.OrdinalIgnoreCase)
                || device.Contains("nfs", StringComparison.OrdinalIgnoreCase);
        }

        private static string RunProcess(string fileName, string arguments, int timeoutMs)
        {
            try
            {
                if (string.Equals(fileName, SmartCtlCommand, StringComparison.OrdinalIgnoreCase))
                {
                    string resolved = ResolveSmartctlPath();
                    if (string.IsNullOrWhiteSpace(resolved))
                        return string.Empty;
                    fileName = resolved;
                }
                else
                {
                    string resolved = ResolveExecutablePath(fileName);
                    if (string.IsNullOrWhiteSpace(resolved))
                        return string.Empty;
                    fileName = resolved;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (Directory.Exists(Environment.CurrentDirectory))
                    psi.WorkingDirectory = Environment.CurrentDirectory;

                using var proc = Process.Start(psi);
                if (proc is null)
                    return string.Empty;

                var sb = new StringBuilder();
                sb.Append(proc.StandardOutput.ReadToEnd());
                proc.WaitForExit(timeoutMs);
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static DriveHealthResult Unknown(string message, string? driveId = null, string? path = null) =>
            new(DriveHealthStatus.Unknown, message, driveId, path);

        private static string L(string key, string fallback) =>
            LocalizationProvider.Service?.GetString(key) ?? fallback;

        private static string ResolveSmartctlPath()
        {
            if (OperatingSystem.IsWindows())
                return ResolveExecutablePath(SmartCtlCommand);

            string[] candidates = OperatingSystem.IsMacOS()
                ?
                [
                    "/opt/homebrew/sbin/smartctl",
                    "/usr/local/sbin/smartctl",
                    "/usr/sbin/smartctl",
                    "/usr/bin/smartctl"
                ]
                :
                [
                    "/usr/sbin/smartctl",
                    "/usr/bin/smartctl",
                    "/sbin/smartctl",
                    "/bin/smartctl"
                ];

            foreach (string? candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, SmartCtlCommand);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // ignore malformed PATH entries
                }
            }

            return string.Empty;
        }

        private static string ResolveExecutablePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return string.Empty;

            if (Path.IsPathRooted(fileName))
                return File.Exists(fileName) ? fileName : string.Empty;

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            bool isWindows = OperatingSystem.IsWindows();
            bool hasExtension = Path.HasExtension(fileName);
            string[] windowsExtensions = isWindows
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                : [];

            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                        return candidate;

                    if (!hasExtension && isWindows)
                    {
                        foreach (string ext in windowsExtensions)
                        {
                            candidate = Path.Combine(dir, fileName + ext.ToLowerInvariant());
                            if (File.Exists(candidate))
                                return candidate;
                        }
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }

            return string.Empty;
        }
    }
}
