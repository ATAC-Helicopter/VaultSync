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
    private readonly CredentialVault _vault = CredentialVault.Instance;

    public DestinationResolution PrepareDestination(BackupDestination dest, NetworkCredentialProfile? profile)
    {
        var alias = DisplayName(dest);
        Console.WriteLine($"[NetworkMount] PrepareDestination: alias='{alias}', path='{dest.Path}', preMounted={dest.PreMounted}, autoMount={dest.AutoMount}, autoUnmount={dest.AutoUnmount}");

        var normalizedPath = NormalizePath(dest.Path, out var normalizeError);
        if (!string.IsNullOrWhiteSpace(normalizeError))
        {
            return DestinationResolution.CreateFailure(dest, normalizeError);
        }

        if (dest.PreMounted)
        {
            Console.WriteLine($"[NetworkMount] Using pre-mounted path for '{alias}'.");
            return Directory.Exists(normalizedPath)
                ? CreateSuccessWithKeepAlive(dest, normalizedPath, mounted: false, $"Using pre-mounted path '{normalizedPath}'")
                : DestinationResolution.CreateFailure(dest, $"Destination '{alias}' is marked pre-mounted but is not accessible.");
        }

        var isNetwork = IsNetworkPath(normalizedPath);
        if (!isNetwork)
        {
            try
            {
                Directory.CreateDirectory(normalizedPath);
                Console.WriteLine($"[NetworkMount] Using local path '{normalizedPath}'.");
                return CreateSuccessWithKeepAlive(dest, normalizedPath, mounted: false, $"Using local path '{normalizedPath}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetworkMount] Local path '{normalizedPath}' failed: {ex.Message}");
                return DestinationResolution.CreateFailure(dest, $"Cannot use destination '{alias}': {ex.Message}");
            }
        }

        if (!dest.AutoMount)
        {
            Console.WriteLine($"[NetworkMount] Auto-mount disabled for '{alias}'.");
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

        Console.WriteLine($"[NetworkMount] Attempting auto-mount for '{alias}' using profile '{profile?.Name ?? "none"}'.");
        var password = profile is null
            ? null
            : _vault.GetSecret(profile.KeyRef, profile.Username, profile.UseKeychain, profile.Password);

        if (OperatingSystem.IsWindows())
        {
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

            return MountMacShare(dest, normalizedPath, profile, password);
        }

        return DestinationResolution.CreateFailure(dest, "Auto-mount is only supported on Windows and macOS.");
    }

    public void Cleanup(DestinationResolution resolution)
    {
        if (!resolution.MountedByUs || !resolution.Destination.AutoUnmount)
            return;

        Console.WriteLine($"[NetworkMount] Auto-unmounting '{DisplayName(resolution.Destination)}' ({resolution.EffectivePath}).");
        if (OperatingSystem.IsWindows())
        {
            DisconnectWindows(resolution);
        }
        else if (OperatingSystem.IsMacOS())
        {
            UnmountMac(resolution);
        }
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

        var username = BuildWindowsUsername(profile);

        try
        {
            var firstAttempt = TryNetUseConnect(normalizedPath, username, password);
            Console.WriteLine($"[NetworkMount] net use connect attempt exit={firstAttempt.ExitCode}.");
            if (firstAttempt.ExitCode == 0)
                return CreateSuccessWithKeepAlive(dest, normalizedPath, mounted: true, $"Mounted {DisplayName(dest)}");

            var detail = FormatNetUseError(firstAttempt);
            if (IsError1219(detail))
            {
                // Error 1219 usually means there is an existing connection to the same server with different credentials.
                // Best-effort disconnect for this share/server and retry once.
                TryNetUseDelete(normalizedPath);
                if (TryParseShare(normalizedPath, out var host, out _))
                {
                    TryNetUseDelete($@"\\{host}");
                }

                var secondAttempt = TryNetUseConnect(normalizedPath, username, password);
                Console.WriteLine($"[NetworkMount] net use retry exit={secondAttempt.ExitCode}.");
                if (secondAttempt.ExitCode == 0)
                    return CreateSuccessWithKeepAlive(dest, normalizedPath, mounted: true, $"Mounted {DisplayName(dest)}");

                var retryDetail = FormatNetUseError(secondAttempt);
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
        var psi = CreateHiddenProcessStartInfo("net");

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
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        return new NetUseResult(proc.ExitCode, stdout, stderr);
    }

    private static void TryNetUseDelete(string uncOrServer)
    {
        try
        {
            var psi = CreateHiddenProcessStartInfo("net");

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

        var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        detail = detail.Replace("\r", string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(detail))
            return result.ExitCode < 0 ? "Unable to start 'net use'." : $"exit {result.ExitCode}";

        var firstLine = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? detail : firstLine;
    }

    private static bool IsError1219(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return false;

        // Works for localized "Errore di sistema 1219" and English "System error 1219".
        return detail.Contains("1219", StringComparison.Ordinal);
    }

    private static DestinationResolution MountMacShare(
        BackupDestination dest,
        string normalizedPath,
        NetworkCredentialProfile? profile,
        string? password)
    {
        if (!TryParseShareWithSubpath(normalizedPath, out var shareHost, out var shareName, out var shareSubPath))
        {
            return DestinationResolution.CreateFailure(dest, "Destination must be an smb:// or UNC path for auto-mount.");
        }

        var mountRoot = GetMacMountRoot();
        try
        {
            Directory.CreateDirectory(mountRoot);
        }
        catch (Exception ex)
        {
            return DestinationResolution.CreateFailure(dest, $"Unable to create mount root '{mountRoot}': {ex.Message}");
        }

        var mountPoint = Path.Combine(mountRoot, string.IsNullOrWhiteSpace(dest.Alias) ? shareName : Slugify(dest.Alias!));
        if (!Directory.Exists(mountPoint))
        {
            try
            {
                Directory.CreateDirectory(mountPoint);
            }
            catch (Exception ex)
            {
                var existing = FindExistingMountPoint(shareName, mountRoot);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    mountPoint = existing;
                }
                else
                {
                    return DestinationResolution.CreateFailure(dest, $"Unable to create mount point '{mountPoint}': {ex.Message}");
                }
            }
        }

        if (TryGetMountedSharePath(shareHost, shareName, mountPoint, out var existingMount))
        {
            mountPoint = existingMount;
            Console.WriteLine($"[NetworkMount] Share already mounted for '{DisplayName(dest)}' at '{mountPoint}'.");
            if (!IsSmbfsMountPoint(mountPoint, out var mountLine))
            {
                return DestinationResolution.CreateFailure(dest, $"Mount point '{mountPoint}' is not an SMB mount.");
            }
            if (!string.IsNullOrWhiteSpace(mountLine))
            {
                Console.WriteLine($"[NetworkMount] SMB mount detected: {mountLine}");
            }
            var effectivePath = AppendShareSubPath(mountPoint, shareSubPath);
            return CreateSuccessWithKeepAlive(dest, effectivePath, mounted: false, $"Mounted {DisplayName(dest)}");
        }

        if (!string.IsNullOrWhiteSpace(password) && profile is not null && string.IsNullOrWhiteSpace(profile.Username))
        {
            return DestinationResolution.CreateFailure(dest, "Username is required to mount with stored credentials.");
        }

        var userPart = profile is null || string.IsNullOrWhiteSpace(profile.Username)
            ? "guest"
            : profile.Username;

        var passwordPart = string.IsNullOrWhiteSpace(password)
            ? string.Empty
            : ":" + Uri.EscapeDataString(password);

        var share = $"//{userPart}{passwordPart}@{shareHost}/{shareName}";
        var shareDisplay = $"//{userPart}@{shareHost}/{shareName}";

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
                return DestinationResolution.CreateFailure(dest, "Unable to start mount_smbfs.");

            proc.WaitForExit(10_000);

            if (proc.ExitCode != 0)
            {
                var stderr = proc.StandardError.ReadToEnd();
                var sanitized = SanitizeMountError(stderr, password, share, shareDisplay);
                Console.WriteLine($"[NetworkMount] mount_smbfs failed for '{DisplayName(dest)}': {sanitized.Trim()}");
                if (TryGetMountedSharePath(shareHost, shareName, mountPoint, out var existingMountAfterFail))
                {
                    mountPoint = existingMountAfterFail;
                    Console.WriteLine($"[NetworkMount] Share already mounted for '{DisplayName(dest)}' at '{mountPoint}'.");
                    if (!IsSmbfsMountPoint(mountPoint, out var mountLine))
                    {
                        return DestinationResolution.CreateFailure(dest, $"Mount point '{mountPoint}' is not an SMB mount.");
                    }
                    if (!string.IsNullOrWhiteSpace(mountLine))
                    {
                        Console.WriteLine($"[NetworkMount] SMB mount detected: {mountLine}");
                    }
                    var effectivePath = AppendShareSubPath(mountPoint, shareSubPath);
                    return CreateSuccessWithKeepAlive(dest, effectivePath, mounted: false, $"Mounted {DisplayName(dest)}");
                }

                return DestinationResolution.CreateFailure(dest, $"Mount failed for {DisplayName(dest)}: {sanitized}".Trim());
            }

            Console.WriteLine($"[NetworkMount] Mounted '{DisplayName(dest)}' at '{mountPoint}'.");
            if (!IsSmbfsMountPoint(mountPoint, out var mountInfo))
            {
                return DestinationResolution.CreateFailure(dest, $"Mount point '{mountPoint}' is not an SMB mount.");
            }
            if (!string.IsNullOrWhiteSpace(mountInfo))
            {
                Console.WriteLine($"[NetworkMount] SMB mount detected: {mountInfo}");
            }
            var finalPath = AppendShareSubPath(mountPoint, shareSubPath);
            return CreateSuccessWithKeepAlive(dest, finalPath, mounted: true, $"Mounted {DisplayName(dest)}");
        }
        catch (Exception ex)
        {
            return DestinationResolution.CreateFailure(dest, $"Mount failed for {DisplayName(dest)}: {ex.Message}");
        }
    }

    private static DestinationResolution MountMacNfsShare(
        BackupDestination dest,
        string normalizedPath)
    {
        if (!TryParseNfs(normalizedPath, out var host, out var exportPath))
        {
            return DestinationResolution.CreateFailure(dest, "Destination must be an nfs://host/export path for auto-mount.");
        }

        var exportCandidates = BuildNfsExportCandidates(exportPath);
        if (exportCandidates.Count == 0)
        {
            return DestinationResolution.CreateFailure(dest, "Destination must be an nfs://host/export path for auto-mount.");
        }

        var mountRoot = GetMacMountRoot();
        try
        {
            Directory.CreateDirectory(mountRoot);
        }
        catch (Exception ex)
        {
            return DestinationResolution.CreateFailure(dest, $"Unable to create mount root '{mountRoot}': {ex.Message}");
        }

        var nameHint = string.IsNullOrWhiteSpace(dest.Alias)
            ? Path.GetFileName(exportPath.TrimEnd('/'))
            : dest.Alias!;
        if (string.IsNullOrWhiteSpace(nameHint))
            nameHint = "nfs-share";

        var mountPoint = Path.Combine(mountRoot, Slugify(nameHint));
        if (!Directory.Exists(mountPoint))
        {
            try
            {
                Directory.CreateDirectory(mountPoint);
            }
            catch (Exception ex)
            {
                return DestinationResolution.CreateFailure(dest, $"Unable to create mount point '{mountPoint}': {ex.Message}");
            }
        }

        if (IsNfsMountPoint(mountPoint, out var mountLine))
        {
            if (!string.IsNullOrWhiteSpace(mountLine))
            {
                Console.WriteLine($"[NetworkMount] NFS mount detected: {mountLine}");
            }
            var effectivePath = mountPoint;
            if (TryGetNfsMountSource(mountLine, out var mountedSource))
            {
                foreach (var candidate in exportCandidates)
                {
                    var source = $"{host}:{candidate.ExportPath}";
                    if (!string.Equals(source, mountedSource, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrWhiteSpace(candidate.SubPath))
                    {
                        effectivePath = Path.Combine(mountPoint, candidate.SubPath);
                    }
                    break;
                }
            }
            return CreateSuccessWithKeepAlive(dest, effectivePath, mounted: false, $"Mounted {DisplayName(dest)}");
        }

        foreach (var candidate in exportCandidates)
        {
            var source = $"{host}:{candidate.ExportPath}";
            var psi = new ProcessStartInfo
            {
                FileName               = "/sbin/mount_nfs",
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            psi.ArgumentList.Add(source);
            psi.ArgumentList.Add(mountPoint);

            try
            {
                using var proc = Process.Start(psi);
                if (proc is null)
                    return DestinationResolution.CreateFailure(dest, "Unable to start mount_nfs.");

                proc.WaitForExit(10_000);

                if (proc.ExitCode != 0)
                {
                    var stderr = proc.StandardError.ReadToEnd();
                    Console.WriteLine($"[NetworkMount] mount_nfs failed for '{DisplayName(dest)}': {stderr.Trim()}");
                    continue;
                }

                Console.WriteLine($"[NetworkMount] Mounted '{DisplayName(dest)}' at '{mountPoint}'.");
                if (!IsNfsMountPoint(mountPoint, out var mountInfo))
                {
                    return DestinationResolution.CreateFailure(dest, $"Mount point '{mountPoint}' is not an NFS mount.");
                }
                if (!string.IsNullOrWhiteSpace(mountInfo))
                {
                    Console.WriteLine($"[NetworkMount] NFS mount detected: {mountInfo}");
                }

                var effectivePath = mountPoint;
                if (!string.IsNullOrWhiteSpace(candidate.SubPath))
                {
                    effectivePath = Path.Combine(mountPoint, candidate.SubPath);
                }
                return CreateSuccessWithKeepAlive(dest, effectivePath, mounted: true, $"Mounted {DisplayName(dest)}");
            }
            catch (Exception ex)
            {
                return DestinationResolution.CreateFailure(dest, $"Mount failed for {DisplayName(dest)}: {ex.Message}");
            }
        }

        return DestinationResolution.CreateFailure(dest, $"Mount failed for {DisplayName(dest)}.");
    }

    private static string SanitizeMountError(string stderr, string? password, string share, string shareDisplay)
    {
        var sanitized = stderr ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(password))
        {
            sanitized = sanitized.Replace(password, "******", StringComparison.Ordinal);
            var escaped = Uri.EscapeDataString(password);
            sanitized = sanitized.Replace(escaped, "******", StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(share))
        {
            sanitized = sanitized.Replace(share, shareDisplay, StringComparison.OrdinalIgnoreCase);
        }

        return sanitized;
    }

    private static string GetMacMountRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "Application Support", "VaultSync", "mounts");
    }

    private static bool TryGetMountedSharePath(string host, string share, string mountPoint, out string mountedPath)
    {
        mountedPath = string.Empty;
        if (!OperatingSystem.IsMacOS())
            return false;

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(share))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "/sbin/mount",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            proc.WaitForExit(3_000);
            var output = proc.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
                return false;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (!line.Contains("smbfs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var onIndex = line.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                if (onIndex <= 0)
                    continue;

                var source = line.Substring(0, onIndex).Trim();
                var rest = line.Substring(onIndex + 4);
                var mountedAt = rest.Split(" (", StringSplitOptions.None)[0].Trim();
                if (string.IsNullOrWhiteSpace(mountedAt))
                    continue;

                if (!string.IsNullOrWhiteSpace(mountPoint) &&
                    string.Equals(mountedAt, mountPoint, StringComparison.OrdinalIgnoreCase))
                {
                    mountedPath = mountedAt;
                    return true;
                }

                if (!TryParseShare(source, out var mountedHost, out var mountedShare))
                    continue;

                if (string.Equals(host, mountedHost, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(share, mountedShare, StringComparison.OrdinalIgnoreCase))
                {
                    mountedPath = mountedAt;
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

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "/sbin/mount",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            proc.WaitForExit(3_000);
            var output = proc.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
                return false;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (!line.Contains("smbfs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var onIndex = line.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                if (onIndex <= 0)
                    continue;

                var rest = line[(onIndex + 4)..];
                var mountedAt = rest.Split(" (", StringSplitOptions.None)[0].Trim();
                if (string.IsNullOrWhiteSpace(mountedAt))
                    continue;

                if (string.Equals(mountedAt, mountPoint, StringComparison.OrdinalIgnoreCase))
                {
                    mountLine = line;
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

    private static string? FindExistingMountPoint(string shareName, string mountRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(shareName))
                return null;

            if (Directory.Exists(mountRoot))
            {
                var exact = Path.Combine(mountRoot, shareName);
                if (Directory.Exists(exact))
                    return exact;

                var match = Directory.EnumerateDirectories(mountRoot)
                    .FirstOrDefault(dir =>
                        string.Equals(Path.GetFileName(dir), shareName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }

            const string volumesRoot = "/Volumes";
            if (!Directory.Exists(volumesRoot))
                return null;

            var volumesExact = Path.Combine(volumesRoot, shareName);
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
            var psi = CreateHiddenProcessStartInfo("net");

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
               path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseShare(string raw, out string host, out string share)
    {
        host  = string.Empty;
        share = string.Empty;

        try
        {
            if (raw.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw["smb://".Length..];
            }
            else if (raw.StartsWith(@"\\"))
            {
                raw = raw.TrimStart('\\');
            }
            else if (raw.StartsWith(@"//"))
            {
                raw = raw.TrimStart('/');
            }

            var parts = raw.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                host  = parts[0];
                share = parts[1];

                if (host.Contains('@'))
                {
                    host = host.Split('@').Last();
                }

                if (host.Contains(':'))
                {
                    host = host.Split(':').First();
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

    private static bool TryParseShareWithSubpath(string raw, out string host, out string share, out string subPath)
    {
        host = string.Empty;
        share = string.Empty;
        subPath = string.Empty;

        try
        {
            if (raw.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw["smb://".Length..];
            }
            else if (raw.StartsWith(@"\\"))
            {
                raw = raw.TrimStart('\\');
            }
            else if (raw.StartsWith(@"//"))
            {
                raw = raw.TrimStart('/');
            }

            var parts = raw.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                host = parts[0];
                share = parts[1];

                if (host.Contains('@'))
                {
                    host = host.Split('@').Last();
                }

                if (host.Contains(':'))
                {
                    host = host.Split(':').First();
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

    private static string AppendShareSubPath(string mountPoint, string subPath)
    {
        if (string.IsNullOrWhiteSpace(subPath))
            return mountPoint;

        var cleaned = subPath.Trim().TrimStart('/', '\\');
        if (string.IsNullOrWhiteSpace(cleaned))
            return mountPoint;

        var segments = cleaned.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0
            ? mountPoint
            : Path.Combine(new[] { mountPoint }.Concat(segments).ToArray());
    }

    private static bool TryParseNfs(string raw, out string host, out string exportPath)
    {
        host = string.Empty;
        exportPath = string.Empty;

        try
        {
            if (raw.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                    return false;

                host = uri.Host;
                exportPath = uri.AbsolutePath;
                return !string.IsNullOrWhiteSpace(exportPath);
            }

            var colonIndex = raw.IndexOf(":/", StringComparison.Ordinal);
            if (colonIndex > 0)
            {
                host = raw[..colonIndex];
                exportPath = raw[(colonIndex + 1)..];
                return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(exportPath);
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static IReadOnlyList<(string ExportPath, string SubPath)> BuildNfsExportCandidates(string exportPath)
    {
        var candidates = new List<(string ExportPath, string SubPath)>();
        var trimmed = exportPath.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return candidates;

        trimmed = trimmed.TrimEnd('/');
        if (trimmed.Length == 0)
            trimmed = "/";

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            candidates.Add(("/", string.Empty));
            return candidates;
        }

        for (var i = segments.Length; i >= 1; i--)
        {
            var root = "/" + string.Join('/', segments.Take(i));
            var subPath = i == segments.Length
                ? string.Empty
                : string.Join('/', segments.Skip(i));
            candidates.Add((root, subPath));
        }

        return candidates;
    }

    private static bool TryGetNfsMountSource(string? mountLine, out string source)
    {
        source = string.Empty;
        if (string.IsNullOrWhiteSpace(mountLine))
            return false;

        var onIndex = mountLine.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (onIndex <= 0)
            return false;

        source = mountLine[..onIndex].Trim();
        return !string.IsNullOrWhiteSpace(source);
    }

    private static string NormalizePath(string raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        var trimmed = raw.Trim();

        if (OperatingSystem.IsWindows() && trimmed.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseShareWithSubpath(trimmed, out var host, out var share, out var subPath))
            {
                var unc = $@"\\{host}\{share}";
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
            return dest.Alias!;
        if (!string.IsNullOrWhiteSpace(dest.Path))
            return dest.Path;
        return "Destination";
    }

    private static string Slugify(string input)
    {
        var sb = new StringBuilder();
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch == '-' || ch == '_')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch))
                sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
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

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "/sbin/mount",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            proc.WaitForExit(3_000);
            var output = proc.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
                return false;

            var candidate = string.Empty;
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (!line.Contains("smbfs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var onIndex = line.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                if (onIndex <= 0)
                    continue;

                var rest = line[(onIndex + 4)..];
                var mountedAt = rest.Split(" (", StringSplitOptions.None)[0].Trim();
                if (string.IsNullOrWhiteSpace(mountedAt))
                    continue;

                if (!path.StartsWith(mountedAt, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (mountedAt.Length > candidate.Length)
                    candidate = mountedAt;
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
                Console.WriteLine($"[NetworkMount] SMB keep-alive enabled for '{key}'.");
                return timer;
            });
        }

        private static void KeepAliveTick(string path)
        {
            try
            {
                var markerDir = Path.Combine(path, ".vaultsync");
                var markerPath = Path.Combine(markerDir, ".keepalive");

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
                Console.WriteLine($"[NetworkMount] SMB keep-alive failed for '{path}': {ex.Message}");
            }
        }
    }

    private static bool IsNfsMountPoint(string mountPoint, out string? mountLine)
    {
        mountLine = null;
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(mountPoint))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "/sbin/mount",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            proc.WaitForExit(3_000);
            var output = proc.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
                return false;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (!line.Contains(" nfs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var onIndex = line.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                if (onIndex <= 0)
                    continue;

                var rest = line[(onIndex + 4)..];
                var mountedAt = rest.Split(" (", StringSplitOptions.None)[0].Trim();
                if (string.IsNullOrWhiteSpace(mountedAt))
                    continue;

                if (string.Equals(mountedAt, mountPoint, StringComparison.OrdinalIgnoreCase))
                {
                    mountLine = line;
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
