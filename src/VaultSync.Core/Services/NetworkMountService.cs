using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Concurrent;
using VaultSync.Core.Config;

namespace VaultSync.Core.Services;

public sealed class NetworkMountService
{
    private const string SmbScheme = "smb://";
    public const string PassiveMountDeferredMessage =
        "Not mounted. VaultSync will request access when a backup or explicit destination test needs this destination.";

    private sealed record MacMountEntry(string Source, string MountPoint, string RawLine);

    private readonly Func<NetworkCredentialProfile?, string?> _passwordResolver;

    public NetworkMountService()
        : this(profile => profile is null
            ? null
            : CredentialVault.Instance.GetSecret(
                profile.KeyRef,
                profile.Username,
                profile.UseKeychain,
                profile.Password))
    {
    }

    internal NetworkMountService(Func<NetworkCredentialProfile?, string?> passwordResolver)
    {
        _passwordResolver = passwordResolver ?? throw new ArgumentNullException(nameof(passwordResolver));
    }

    public DestinationResolution PrepareDestination(
        BackupDestination dest,
        NetworkCredentialProfile? profile,
        bool allowAutoMount = true)
    {
        string alias = DisplayName(dest);
        Log($"PrepareDestination: alias='{alias}', path='{dest.Path}', preMounted={dest.PreMounted}, autoMount={dest.AutoMount}, autoUnmount={dest.AutoUnmount}");

        string normalizedPath = NormalizePath(dest.Path, out string? normalizeError);
        if (!string.IsNullOrWhiteSpace(normalizeError))
        {
            return DestinationResolution.CreateFailure(dest, normalizeError);
        }

        if (dest.PreMounted)
        {
            Log($"Using pre-mounted path for '{alias}'.");
            return Directory.Exists(normalizedPath)
                ? CreateSuccessWithKeepAlive(dest, normalizedPath, mounted: false, $"Using pre-mounted path '{normalizedPath}'")
                : DestinationResolution.CreateFailure(dest, $"Destination '{alias}' is marked pre-mounted but is not accessible.");
        }

        if (!IsNetworkPath(normalizedPath))
            return PrepareLocalDestination(dest, alias, normalizedPath);

        if (!dest.AutoMount)
        {
            Log($"Auto-mount disabled for '{alias}'.");
            if (OperatingSystem.IsMacOS() &&
                normalizedPath.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase))
            {
                return DestinationResolution.CreateFailure(
                    dest,
                    "NFS destinations on macOS must use a pre-mounted local path. Mount the share first and set the destination to that mount point.");
            }
            return Directory.Exists(normalizedPath)
                ? CreateSuccessWithKeepAlive(dest, normalizedPath, mounted: false, $"Using reachable network path '{normalizedPath}'")
                : DestinationResolution.CreateFailure(dest, $"Destination '{alias}' is unreachable and auto-mount is disabled.");
        }

        Log($"Attempting auto-mount for '{alias}' using profile '{profile?.Name ?? "none"}'.");

        // Background health/maintenance work must never unlock a credential store
        // or establish a new connection. macOS gets one extra chance below to
        // reuse an already-mounted SMB share without reading the credential.
        if (!allowAutoMount && !OperatingSystem.IsMacOS())
            return DestinationResolution.CreateFailure(dest, PassiveMountDeferredMessage);

        if (OperatingSystem.IsWindows())
        {
            string? password = ResolvePassword(profile);
            return ConnectWindowsShare(dest, normalizedPath, profile, password);
        }

        if (OperatingSystem.IsMacOS())
        {
            if (normalizedPath.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase))
            {
                return DestinationResolution.CreateFailure(
                    dest,
                    "NFS auto-mount is not supported on macOS. Pre-mount the share and use the local mount path with Auto-mount disabled.");
            }

            return MountMacShare(dest, normalizedPath, profile, allowAutoMount);
        }

        return DestinationResolution.CreateFailure(dest, "Auto-mount is only supported on Windows and macOS.");
    }

    public static void Cleanup(DestinationResolution resolution)
    {
        if (!resolution.MountedByUs || !resolution.Destination.AutoUnmount)
            return;

        Log($"Auto-unmounting '{DisplayName(resolution.Destination)}' ({resolution.EffectivePath}).");
        if (OperatingSystem.IsWindows())
        {
            DisconnectWindows(resolution);
        }
        else if (OperatingSystem.IsMacOS())
        {
            UnmountMac(resolution);
        }
    }

    private static DestinationResolution PrepareLocalDestination(
        BackupDestination destination,
        string alias,
        string normalizedPath)
    {
        if (IsMacVolumesPath(normalizedPath))
            return PrepareMacVolumeDestination(destination, alias, normalizedPath);

        try
        {
            Directory.CreateDirectory(normalizedPath);
            Log($"Using local path '{normalizedPath}'.");
            return CreateSuccessWithKeepAlive(destination, normalizedPath, mounted: false, $"Using local path '{normalizedPath}'");
        }
        catch (Exception ex)
        {
            Log($"Local path '{normalizedPath}' failed: {ex.Message}");
            return DestinationResolution.CreateFailure(destination, $"Cannot use destination '{alias}': {ex.Message}");
        }
    }

    private static DestinationResolution PrepareMacVolumeDestination(
        BackupDestination destination,
        string alias,
        string normalizedPath)
    {
        if (IsAccessibleDirectory(normalizedPath, out string? accessError))
        {
            Log($"Using macOS mounted volume path '{normalizedPath}'.");
            return CreateSuccessWithKeepAlive(destination, normalizedPath, mounted: false, $"Using mounted volume path '{normalizedPath}'");
        }

        string detail = string.IsNullOrWhiteSpace(accessError)
            ? "The mounted volume path is not accessible."
            : accessError;
        Log($"Mounted volume path '{normalizedPath}' failed: {detail}");
        return DestinationResolution.CreateFailure(
            destination,
            $"Cannot use destination '{alias}': {detail} Use a reachable /Volumes mount point, or configure the destination as smb://host/share for auto-mount.");
    }

    private string? ResolvePassword(NetworkCredentialProfile? profile)
    {
        return _passwordResolver(profile);
    }

    private static DestinationResolution ConnectWindowsShare(
        BackupDestination dest,
        string normalizedPath,
        NetworkCredentialProfile? profile,
        string? password)
    {
        if (!IsNetworkPath(normalizedPath))
        {
            return DestinationResolution.CreateFailure(dest, "Invalid UNC path for Windows mount.");
        }

        if (profile is null)
        {
            return DestinationResolution.CreateFailure(dest, "Auto-mount requires a credential profile.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return DestinationResolution.CreateFailure(dest, $"No password available for credential '{profile.Name}'.");
        }

        string? username = BuildWindowsUsername(profile);

        try
        {
            NetUseResult firstAttempt = TryNetUseConnect(normalizedPath, username, password);
            Log($"net use connect attempt exit={firstAttempt.ExitCode}.");
            if (firstAttempt.ExitCode == 0)
                return CreateSuccessWithKeepAlive(dest, normalizedPath, mounted: true, $"Mounted {DisplayName(dest)}");

            string detail = FormatNetUseError(firstAttempt);
            if (IsError1219(detail))
            {
                // Error 1219 usually means there is an existing connection to the same server with different credentials.
                // Best-effort disconnect for this share/server and retry once.
                TryNetUseDelete(normalizedPath);
                if (TryParseShare(normalizedPath, out string? host, out _))
                {
                    TryNetUseDelete($@"\\{host}");
                }

                NetUseResult secondAttempt = TryNetUseConnect(normalizedPath, username, password);
                Log($"net use retry exit={secondAttempt.ExitCode}.");
                if (secondAttempt.ExitCode == 0)
                    return CreateSuccessWithKeepAlive(dest, normalizedPath, mounted: true, $"Mounted {DisplayName(dest)}");

                string retryDetail = FormatNetUseError(secondAttempt);
                return DestinationResolution.CreateFailure(
                    dest,
                    $"Windows error 1219: another connection to this server is already using different credentials. Disconnect existing connections to that server and retry. Details: {retryDetail}");
            }

            return DestinationResolution.CreateFailure(dest, $"Failed to mount {DisplayName(dest)}: {detail}");
        }
        catch (Exception ex)
        {
            return DestinationResolution.CreateFailure(dest, $"Failed to mount {DisplayName(dest)}: {ex.Message}");
        }
    }

    private sealed record NetUseResult(int ExitCode, string Stdout, string Stderr);

    private static ProcessStartInfo CreateHiddenProcessStartInfo(string fileName)
    {
        return new ProcessStartInfo
        {
            FileName               = fileName,
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WindowStyle            = ProcessWindowStyle.Hidden
        };
    }

    private static NetUseResult TryNetUseConnect(string uncPath, string? username, string password)
    {
        ProcessStartInfo psi = CreateHiddenProcessStartInfo("net");

        psi.ArgumentList.Add("use");
        psi.ArgumentList.Add(uncPath);
        psi.ArgumentList.Add(password);

        if (!string.IsNullOrWhiteSpace(username))
        {
            psi.ArgumentList.Add($"/user:{username}");
        }

        psi.ArgumentList.Add("/persistent:no");

        using var proc = Process.Start(psi);
        if (proc is null)
            return new NetUseResult(-1, string.Empty, "Unable to start 'net use'.");

        proc.WaitForExit(10_000);
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        return new NetUseResult(proc.ExitCode, stdout, stderr);
    }

    private static void TryNetUseDelete(string uncOrServer)
    {
        try
        {
            ProcessStartInfo psi = CreateHiddenProcessStartInfo("net");

            psi.ArgumentList.Add("use");
            psi.ArgumentList.Add(uncOrServer);
            psi.ArgumentList.Add("/delete");
            psi.ArgumentList.Add("/y");

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5_000);
        }
        catch
        {
            // best-effort
        }
    }

    private static string? BuildWindowsUsername(NetworkCredentialProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Username))
            return null;

        // If the user already provided DOMAIN\user or user@domain, keep it as-is.
        if (profile.Username.Contains('\\') || profile.Username.Contains('@'))
            return profile.Username;

        if (!string.IsNullOrWhiteSpace(profile.Domain))
            return $"{profile.Domain}\\{profile.Username}";

        return profile.Username;
    }

    private static string FormatNetUseError(NetUseResult result)
    {
        if (result.ExitCode == 0)
            return string.Empty;

        string detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        detail = detail.Replace("\r", string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(detail))
            return result.ExitCode < 0 ? "Unable to start 'net use'." : $"exit {result.ExitCode}";

        string? firstLine = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? detail : firstLine;
    }

    private static bool IsError1219(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return false;

        // Works for localized "Errore di sistema 1219" and English "System error 1219".
        return detail.Contains("1219", StringComparison.Ordinal);
    }

    private DestinationResolution MountMacShare(
        BackupDestination dest,
        string normalizedPath,
        NetworkCredentialProfile? profile,
        bool allowAutoMount)
    {
        if (!TryParseShareWithSubpath(normalizedPath, out string shareHost, out string shareName, out string shareSubPath))
        {
            return DestinationResolution.CreateFailure(dest, "Destination must be an smb:// or UNC path for auto-mount.");
        }

        if (!TryPrepareMacMountPoint(dest, shareName, out string mountPoint, out string? mountError))
            return DestinationResolution.CreateFailure(dest, mountError ?? "Unable to prepare the SMB mount point.");

        if (TryGetMountedSharePath(shareHost, shareName, mountPoint, out string? existingMount))
        {
            return CreateMacMountResolution(dest, existingMount, shareSubPath, mountedByUs: false);
        }

        if (!allowAutoMount)
            return DestinationResolution.CreateFailure(dest, PassiveMountDeferredMessage);

        // Only unlock the native credential when a new mount is actually needed.
        // Existing SMB mounts remain usable after login without a Keychain prompt.
        string? password = ResolvePassword(profile);

        if (!string.IsNullOrWhiteSpace(password) && profile is not null && string.IsNullOrWhiteSpace(profile.Username))
        {
            return DestinationResolution.CreateFailure(dest, "Username is required to mount with stored credentials.");
        }

        string userPart = profile is null || string.IsNullOrWhiteSpace(profile.Username)
            ? "guest"
            : profile.Username;

        return RunMacMount(dest, shareHost, shareName, shareSubPath, mountPoint, password, userPart);
    }

    private static bool TryPrepareMacMountPoint(
        BackupDestination destination,
        string shareName,
        out string mountPoint,
        out string? error)
    {
        string mountRoot = GetMacMountRoot();
        mountPoint = string.Empty;
        error = null;
        try
        {
            Directory.CreateDirectory(mountRoot);
        }
        catch (Exception ex)
        {
            error = $"Unable to create mount root '{mountRoot}': {ex.Message}";
            return false;
        }

        string mountName = string.IsNullOrWhiteSpace(destination.Alias)
            ? shareName
            : Slugify(destination.Alias);
        mountPoint = Path.Combine(mountRoot, mountName);
        if (Directory.Exists(mountPoint))
            return true;

        try
        {
            Directory.CreateDirectory(mountPoint);
            return true;
        }
        catch (Exception ex)
        {
            string? existing = FindExistingMountPoint(shareName, mountRoot);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                mountPoint = existing;
                return true;
            }

            error = $"Unable to create mount point '{mountPoint}': {ex.Message}";
            return false;
        }
    }

    private static DestinationResolution RunMacMount(
        BackupDestination destination,
        string shareHost,
        string shareName,
        string shareSubPath,
        string mountPoint,
        string? password,
        string userPart)
    {
        string passwordPart = string.IsNullOrWhiteSpace(password)
            ? string.Empty
            : ":" + Uri.EscapeDataString(password);
        string share = $"//{userPart}{passwordPart}@{shareHost}/{shareName}";
        string shareDisplay = $"//{userPart}@{shareHost}/{shareName}";
        var psi = new ProcessStartInfo
        {
            FileName               = "/sbin/mount_smbfs",
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        psi.ArgumentList.Add(share);
        psi.ArgumentList.Add(mountPoint);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return DestinationResolution.CreateFailure(destination, "Unable to start mount_smbfs.");

            proc.WaitForExit(10_000);

            if (proc.ExitCode != 0)
            {
                string stderr = proc.StandardError.ReadToEnd();
                string sanitized = SanitizeMountError(stderr, password, share, shareDisplay);
                Log($"mount_smbfs failed for '{DisplayName(destination)}': {sanitized.Trim()}");
                if (TryGetMountedSharePath(shareHost, shareName, mountPoint, out string? existingMountAfterFail))
                {
                    return CreateMacMountResolution(destination, existingMountAfterFail, shareSubPath, mountedByUs: false);
                }

                return DestinationResolution.CreateFailure(destination, $"Mount failed for {DisplayName(destination)}: {sanitized}".Trim());
            }

            return CreateMacMountResolution(destination, mountPoint, shareSubPath, mountedByUs: true);
        }
        catch (Exception ex)
        {
            return DestinationResolution.CreateFailure(destination, $"Mount failed for {DisplayName(destination)}: {ex.Message}");
        }
    }

    private static DestinationResolution CreateMacMountResolution(
        BackupDestination destination,
        string mountPoint,
        string shareSubPath,
        bool mountedByUs)
    {
        Log($"Share mounted for '{DisplayName(destination)}' at '{mountPoint}'.");
        if (!IsSmbfsMountPoint(mountPoint, out string? mountLine))
            return DestinationResolution.CreateFailure(destination, $"Mount point '{mountPoint}' is not an SMB mount.");

        if (!string.IsNullOrWhiteSpace(mountLine))
            Log($"SMB mount detected: {mountLine}");

        string effectivePath = AppendShareSubPath(mountPoint, shareSubPath);
        return CreateSuccessWithKeepAlive(
            destination,
            effectivePath,
            mountedByUs,
            $"Mounted {DisplayName(destination)}");
    }

    internal static string SanitizeMountError(string stderr, string? password, string share, string shareDisplay)
    {
        string sanitized = stderr ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(share))
        {
            sanitized = sanitized.Replace(share, shareDisplay, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(password))
        {
            sanitized = sanitized.Replace(password, "******", StringComparison.Ordinal);
            string escaped = Uri.EscapeDataString(password);
            sanitized = sanitized.Replace(escaped, "******", StringComparison.Ordinal);
        }

        return sanitized;
    }

    private static string GetMacMountRoot()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "Application Support", "VaultSync", "mounts");
    }

    private static bool TryGetMountedSharePath(string host, string share, string mountPoint, out string mountedPath)
    {
        mountedPath = string.Empty;
        if (!OperatingSystem.IsMacOS())
            return false;

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(share))
            return false;

        if (!TryReadMacSmbMounts(out IReadOnlyList<MacMountEntry> mounts))
            return false;

        try
        {
            foreach (MacMountEntry mount in mounts)
            {
                if (!string.IsNullOrWhiteSpace(mountPoint) &&
                    string.Equals(mount.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase))
                {
                    mountedPath = mount.MountPoint;
                    return true;
                }

                if (!TryParseShare(mount.Source, out string? mountedHost, out string? mountedShare))
                    continue;

                if (string.Equals(host, mountedHost, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(share, mountedShare, StringComparison.OrdinalIgnoreCase))
                {
                    mountedPath = mount.MountPoint;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsSmbfsMountPoint(string mountPoint, out string? mountLine)
    {
        mountLine = null;
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(mountPoint))
            return false;

        if (!TryReadMacSmbMounts(out IReadOnlyList<MacMountEntry> mounts))
            return false;

        try
        {
            MacMountEntry? mount = mounts.FirstOrDefault(
                candidate => string.Equals(candidate.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
            mountLine = mount?.RawLine;
            return mount is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindExistingMountPoint(string shareName, string mountRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(shareName))
                return null;

            if (Directory.Exists(mountRoot))
            {
                string exact = Path.Combine(mountRoot, shareName);
                if (Directory.Exists(exact))
                    return exact;

                string? match = Directory.EnumerateDirectories(mountRoot)
                    .FirstOrDefault(dir =>
                        string.Equals(Path.GetFileName(dir), shareName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }

            const string volumesRoot = "/Volumes";
            if (!Directory.Exists(volumesRoot))
                return null;

            string volumesExact = Path.Combine(volumesRoot, shareName);
            if (Directory.Exists(volumesExact))
                return volumesExact;

            return Directory.EnumerateDirectories(volumesRoot)
                .FirstOrDefault(dir =>
                    string.Equals(Path.GetFileName(dir), shareName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static void DisconnectWindows(DestinationResolution res)
    {
        try
        {
            ProcessStartInfo psi = CreateHiddenProcessStartInfo("net");

            psi.ArgumentList.Add("use");
            psi.ArgumentList.Add(string.IsNullOrWhiteSpace(res.EffectivePath) ? res.Destination.Path : res.EffectivePath);
            psi.ArgumentList.Add("/delete");
            psi.ArgumentList.Add("/y");

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5_000);
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private static void UnmountMac(DestinationResolution res)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "/sbin/umount",
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            psi.ArgumentList.Add(res.EffectivePath);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5_000);
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(SmbScheme, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMacVolumesPath(string path)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(path))
            return false;

        string normalized = path.Replace('\\', '/').TrimEnd('/');
        return normalized.Equals("/Volumes", StringComparison.Ordinal) ||
               normalized.StartsWith("/Volumes/", StringComparison.Ordinal);
    }

    private static bool IsAccessibleDirectory(string path, out string? error)
    {
        error = null;
        try
        {
            if (!Directory.Exists(path))
            {
                error = "The directory does not exist.";
                return false;
            }

            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseShare(string raw, out string host, out string share)
    {
        return TryParseShareWithSubpath(raw, out host, out share, out _);
    }

    internal static bool TryParseShareWithSubpath(string raw, out string host, out string share, out string subPath)
    {
        host = string.Empty;
        share = string.Empty;
        subPath = string.Empty;

        try
        {
            if (raw.StartsWith(SmbScheme, StringComparison.OrdinalIgnoreCase))
            {
                raw = raw[SmbScheme.Length..];
            }
            else if (raw.StartsWith(@"\\"))
            {
                raw = raw.TrimStart('\\');
            }
            else if (raw.StartsWith(@"//"))
            {
                raw = raw.TrimStart('/');
            }

            string[] parts = raw.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                host = parts[0];
                share = parts[1];

                if (host.Contains('@'))
                {
                    string[] hostParts = host.Split('@');
                    host = hostParts[^1];
                }

                if (host.Contains(':'))
                {
                    host = host.Split(':')[0];
                }

                if (parts.Length > 2)
                {
                    subPath = string.Join('/', parts.Skip(2));
                }

                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryReadMacSmbMounts(out IReadOnlyList<MacMountEntry> mounts)
    {
        mounts = [];
        if (!OperatingSystem.IsMacOS())
            return false;

        try
        {
            ProcessStartInfo psi = CreateHiddenProcessStartInfo("/sbin/mount");
            using var process = Process.Start(psi);
            if (process is null)
                return false;

            process.WaitForExit(3_000);
            string output = process.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
                return false;

            string[] lines = output.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            mounts = lines
                .Select(ParseMacSmbMount)
                .Where(entry => entry is not null)
                .Cast<MacMountEntry>()
                .ToList();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MacMountEntry? ParseMacSmbMount(string line)
    {
        return TryParseMacSmbMountLine(line, out string source, out string mountPoint)
            ? new MacMountEntry(source, mountPoint, line)
            : null;
    }

    internal static bool TryParseMacSmbMountLine(
        string line,
        out string source,
        out string mountPoint)
    {
        source = string.Empty;
        mountPoint = string.Empty;
        if (string.IsNullOrWhiteSpace(line) ||
            !line.Contains("smbfs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int onIndex = line.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (onIndex <= 0)
            return false;

        source = line[..onIndex].Trim();
        string rest = line[(onIndex + 4)..];
        mountPoint = rest.Split(" (", StringSplitOptions.None)[0].Trim();
        return !string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(mountPoint);
    }

    internal static string AppendShareSubPath(string mountPoint, string subPath)
    {
        if (string.IsNullOrWhiteSpace(subPath))
            return mountPoint;

        string cleaned = subPath.Trim().TrimStart('/', '\\');
        if (string.IsNullOrWhiteSpace(cleaned))
            return mountPoint;

        string[] segments = cleaned.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0
            ? mountPoint
            : Path.Combine([mountPoint, .. segments]);
    }

    private static string NormalizePath(string raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        string trimmed = raw.Trim();

        if (OperatingSystem.IsWindows() && trimmed.StartsWith(SmbScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseShareWithSubpath(trimmed, out string? host, out string? share, out string? subPath))
            {
                string unc = $@"\\{host}\{share}";
                if (!string.IsNullOrWhiteSpace(subPath))
                    unc = Path.Combine(unc, subPath.Replace('/', Path.DirectorySeparatorChar));
                return unc;
            }

            error = "Destination must be a UNC path on Windows.";
        }

        return trimmed;
    }

    private static string DisplayName(BackupDestination dest)
    {
        if (!string.IsNullOrWhiteSpace(dest.Alias))
            return dest.Alias;
        if (!string.IsNullOrWhiteSpace(dest.Path))
            return dest.Path;
        return "Destination";
    }

    private static void Log(string message)
    {
        RuntimeVaultLogger.Instance.Info($"[NetworkMount] {message}");
    }

    internal static string Slugify(string input)
    {
        var sb = new StringBuilder();
        foreach (char ch in input)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch == '-' || ch == '_')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch))
                sb.Append('-');
        }

        string slug = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "vaultsync-share" : slug;
    }

    private static DestinationResolution CreateSuccessWithKeepAlive(
        BackupDestination dest,
        string path,
        bool mounted,
        string message)
    {
        StartMacSmbKeepAlive(path);
        return DestinationResolution.CreateSuccess(dest, path, mounted, message);
    }

    private static void StartMacSmbKeepAlive(string effectivePath)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        if (!TryResolveSmbMountRoot(effectivePath, out _))
            return;

        KeepAliveRegistry.Start(effectivePath);
    }

    private static bool TryResolveSmbMountRoot(string path, out string mountRoot)
    {
        mountRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!TryReadMacSmbMounts(out IReadOnlyList<MacMountEntry> mounts))
            return false;

        try
        {
            string candidate = string.Empty;
            foreach (string mountPoint in mounts.Select(mount => mount.MountPoint))
            {
                if (!path.StartsWith(mountPoint, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (mountPoint.Length > candidate.Length)
                    candidate = mountPoint;
            }

            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            mountRoot = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static class KeepAliveRegistry
    {
        private static readonly ConcurrentDictionary<string, Timer> Timers = new(StringComparer.OrdinalIgnoreCase);

        public static void Start(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            Timers.GetOrAdd(path, key =>
            {
                var timer = new Timer(_ => KeepAliveTick(key), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
                Log($"SMB keep-alive enabled for '{key}'.");
                return timer;
            });
        }

        private static void KeepAliveTick(string path)
        {
            try
            {
                string markerDir = Path.Combine(path, ".vaultsync");
                string markerPath = Path.Combine(markerDir, ".keepalive");

                try
                {
                    Directory.CreateDirectory(markerDir);
                    File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
                }
                catch
                {
                    if (Directory.Exists(path))
                    {
                        _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"SMB keep-alive failed for '{path}': {ex.Message}");
            }
        }
    }

}

public sealed record DestinationResolution(
    BackupDestination Destination,
    string EffectivePath,
    bool MountedByUs,
    string Message,
    bool IsSuccess)
{
    public static DestinationResolution CreateSuccess(BackupDestination dest, string path, bool mounted, string message) =>
        new(dest, path, mounted, message, true);

    public static DestinationResolution CreateFailure(BackupDestination dest, string message) =>
        new(dest, dest.Path, false, message, false);
}
