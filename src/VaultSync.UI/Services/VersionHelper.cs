using System;

namespace VaultSync.UI.Services
{
    internal static class VersionHelper
    {
        public static Version? TryParse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var trimmed = value.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[1..];

            var separatorIndex = trimmed.IndexOfAny(new[] { '-', '+' });
            if (separatorIndex >= 0)
                trimmed = trimmed[..separatorIndex];

            trimmed = trimmed.Trim();
            if (Version.TryParse(trimmed, out var version))
                return version;

            return null;
        }
    }
}
