using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VaultSync.Core.Config;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public static class DestinationIdentityService
{
    public static string GetId(BackupDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var normalizedPath = NormalizePath(destination.Path);
        var normalizedCredential = (destination.CredentialName ?? string.Empty).Trim().ToLowerInvariant();
        var mountMode = destination.PreMounted ? "premounted" : "managed";
        var payload = $"{normalizedPath}|{normalizedCredential}|{mountMode}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"dest-{Convert.ToHexString(hash).ToLowerInvariant()[..24]}";
    }

    public static string NormalizePreferredDestinationId(string? preferredDestinationId, IEnumerable<BackupDestination>? destinations)
    {
        var raw = preferredDestinationId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        if (string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (string.Equals(raw, Project.DestinationAllId, StringComparison.OrdinalIgnoreCase))
            return Project.DestinationAllId;

        var list = destinations?.ToList() ?? new List<BackupDestination>();
        if (list.Count == 0)
            return raw;

        var exact = list.FirstOrDefault(dest =>
            string.Equals(GetId(dest), raw, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return GetId(exact);

        var legacy = list.FirstOrDefault(dest =>
            string.Equals(dest.Alias ?? string.Empty, raw, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dest.Path ?? string.Empty, raw, StringComparison.OrdinalIgnoreCase));
        return legacy is null ? raw : GetId(legacy);
    }

    public static BackupDestination? FindByPreferredDestinationId(IEnumerable<BackupDestination>? destinations, string? preferredDestinationId)
    {
        var normalized = NormalizePreferredDestinationId(preferredDestinationId, destinations);
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, Project.DestinationAllId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return destinations?.FirstOrDefault(dest =>
            string.Equals(GetId(dest), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string? path)
    {
        var raw = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var slashNormalized = raw.Replace('/', '\\');
        try
        {
            if (slashNormalized.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return slashNormalized.TrimEnd('\\').ToLowerInvariant();
            }

            var fullPath = Path.GetFullPath(slashNormalized);
            return fullPath.TrimEnd('\\').ToLowerInvariant();
        }
        catch
        {
            return slashNormalized.TrimEnd('\\').ToLowerInvariant();
        }
    }
}
