using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using VaultSync.Core.Config;

namespace VaultSync.Core.Services;

public sealed class NetworkMountService
{
    private readonly CredentialVault _vault = CredentialVault.Instance;

    public DestinationResolution PrepareDestination(BackupDestination dest, NetworkCredentialProfile? profile)
    {
        var alias = DisplayName(dest);

        if (dest.PreMounted)
        {
            return Directory.Exists(dest.Path)
                ? DestinationResolution.CreateSuccess(dest, dest.Path, mounted: false, $"Using pre-mounted path '{dest.Path}'")
                : DestinationResolution.CreateFailure(dest, $"Destination '{alias}' is marked pre-mounted but is not accessible.");
        }

        var isNetwork = IsNetworkPath(dest.Path);
        if (!isNetwork)
        {
            try
            {
                Directory.CreateDirectory(dest.Path);
                return DestinationResolution.CreateSuccess(dest, dest.Path, mounted: false, $"Using local path '{dest.Path}'");
            }
            catch (Exception ex)
            {
                return DestinationResolution.CreateFailure(dest, $"Cannot use destination '{alias}': {ex.Message}");
            }
        }

        if (!dest.AutoMount)
        {
            return Directory.Exists(dest.Path)
                ? DestinationResolution.CreateSuccess(dest, dest.Path, mounted: false, $"Using reachable network path '{dest.Path}'")
                : DestinationResolution.CreateFailure(dest, $"Destination '{alias}' is unreachable and auto-mount is disabled.");
        }

        var password = profile is null
            ? null
            : _vault.GetSecret(profile.KeyRef, profile.Username, profile.UseKeychain, profile.Password);

        if (OperatingSystem.IsWindows())
        {
            return ConnectWindowsShare(dest, profile, password);
        }

        if (OperatingSystem.IsMacOS())
        {
            return MountMacShare(dest, profile, password);
        }

        return DestinationResolution.CreateFailure(dest, "Auto-mount is only supported on Windows and macOS.");
    }

    public void Cleanup(DestinationResolution resolution)
    {
        if (!resolution.MountedByUs || !resolution.Destination.AutoUnmount)
            return;

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
        NetworkCredentialProfile? profile,
        string? password)
    {
        if (!IsNetworkPath(dest.Path))
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

        var psi = new ProcessStartInfo
        {
            FileName               = "net",
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
            UseShellExecute        = false
        };

        psi.ArgumentList.Add("use");
        psi.ArgumentList.Add(dest.Path);

        psi.ArgumentList.Add(password);

        if (profile is not null && !string.IsNullOrWhiteSpace(profile.Username))
        {
            psi.ArgumentList.Add($"/user:{profile.Username}");
        }

        psi.ArgumentList.Add("/persistent:no");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return DestinationResolution.CreateFailure(dest, "Unable to start 'net use'.");

            proc.WaitForExit(10_000);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();

            if (proc.ExitCode == 0)
            {
                return DestinationResolution.CreateSuccess(dest, dest.Path, mounted: true, $"Mounted {DisplayName(dest)}");
            }

            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            detail = string.IsNullOrWhiteSpace(detail) ? $"exit {proc.ExitCode}" : detail.Trim();
            return DestinationResolution.CreateFailure(dest, $"Failed to mount {DisplayName(dest)}: {detail}");
        }
        catch (Exception ex)
        {
            return DestinationResolution.CreateFailure(dest, $"Failed to mount {DisplayName(dest)}: {ex.Message}");
        }
    }

    private static DestinationResolution MountMacShare(
        BackupDestination dest,
        NetworkCredentialProfile? profile,
        string? password)
    {
        if (!TryParseShare(dest.Path, out var shareHost, out var shareName))
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
            UseShellExecute        = false
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
                return DestinationResolution.CreateFailure(dest, $"Mount failed for {DisplayName(dest)}: {stderr}".Trim());
            }

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
            var psi = new ProcessStartInfo
            {
                FileName               = "net",
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false
            };

            psi.ArgumentList.Add("use");
            psi.ArgumentList.Add(res.Destination.Path);
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
                UseShellExecute        = false
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
