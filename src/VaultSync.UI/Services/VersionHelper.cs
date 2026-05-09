using System;

namespace VaultSync.UI.Services
{
    internal static class VersionHelper
    {
        public static string NormalizeIdentity(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string trimmed = value.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[1..];

            int plusIndex = trimmed.IndexOf('+');
            if (plusIndex >= 0)
                trimmed = trimmed[..plusIndex];

            return trimmed.Trim();
        }

        public static string GetPrereleaseLabel(string? value)
        {
            string normalized = NormalizeIdentity(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            int dashIndex = normalized.IndexOf('-');
            return dashIndex >= 0
                ? normalized[(dashIndex + 1)..].Trim()
                : string.Empty;
        }

        public static Version? TryParse(string? value)
        {
            string trimmed = NormalizeIdentity(value);
            if (string.IsNullOrWhiteSpace(trimmed))
                return null;

            int separatorIndex = trimmed.IndexOf('-');
            if (separatorIndex >= 0)
                trimmed = trimmed[..separatorIndex];

            trimmed = trimmed.Trim();
            if (Version.TryParse(trimmed, out Version? version))
                return version;

            return null;
        }
    }
}
