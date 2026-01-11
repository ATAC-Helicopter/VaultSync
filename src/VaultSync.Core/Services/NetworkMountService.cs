using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
                ? DestinationResolution.CreateSuccess(dest, normalizedPath, mounted: false, $"Using pre-mounted path '{normalizedPath}'")
                : DestinationResolution.CreateFailure(dest, $"Destination '{alias}' is marked pre-mounted but is not accessible.");
        }

        var isNetwork = IsNetworkPath(normalizedPath);
        if (!isNetwork)
        {
            try
            {
                Directory.CreateDirectory(normalizedPath);
                Console.WriteLine($"[NetworkMount] Using local path '{normalizedPath}'.");
                return DestinationResolution.CreateSuccess(dest, normalizedPath, mounted: false, $"Using local path '{normalizedPath}'");
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
            return Directory.Exists(normalizedPath)
                ? DestinationResolution.CreateSuccess(dest, normalizedPath, mounted: false, $"Using reachable network path '{normalizedPath}'")
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
                return DestinationResolution.CreateSuccess(dest, normalizedPath, mounted: true, $"Mounted {DisplayName(dest)}");

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
                    return DestinationResolution.CreateSuccess(dest, normalizedPath, mounted: true, $"Mounted {DisplayName(dest)}");

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
        if (!TryParseShare(normalizedPath, out var shareHost, out var shareName))
        {
            return DestinationResolution.CreateFailure(dest, "Destination must be an smb:// or UNC path for auto-mount.");
        }

        var mountPoint = Path.Combine("/Volumes", string.IsNullOrWhiteSpace(dest.Alias) ? shareName : Slugify(dest.Alias!));
        Directory.CreateDirectory(mountPoint);

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
                Console.WriteLine($"[NetworkMount] mount_smbfs failed for '{DisplayName(dest)}': {stderr.Trim()}");
                return DestinationResolution.CreateFailure(dest, $"Mount failed for {DisplayName(dest)}: {stderr}".Trim());
            }

            Console.WriteLine($"[NetworkMount] Mounted '{DisplayName(dest)}' at '{mountPoint}'.");
            return DestinationResolution.CreateSuccess(dest, mountPoint, mounted: true, $"Mounted {DisplayName(dest)}");
        }
        catch (Exception ex)
        {
            return DestinationResolution.CreateFailure(dest, $"Mount failed for {DisplayName(dest)}: {ex.Message}");
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
               path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase);
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
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string NormalizePath(string raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        var trimmed = raw.Trim();

        if (OperatingSystem.IsWindows() && trimmed.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseShare(trimmed, out var host, out var share))
                return $@"\\{host}\{share}";

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
