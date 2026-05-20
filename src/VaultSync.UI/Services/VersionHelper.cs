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

        public static int CompareReleaseIdentities(string? left, string? right)
        {
            string leftNormalized = NormalizeIdentity(left);
            string rightNormalized = NormalizeIdentity(right);

            if (string.IsNullOrWhiteSpace(leftNormalized) && string.IsNullOrWhiteSpace(rightNormalized))
                return 0;
            if (string.IsNullOrWhiteSpace(leftNormalized))
                return -1;
            if (string.IsNullOrWhiteSpace(rightNormalized))
                return 1;

            Version? leftVersion = TryParse(leftNormalized);
            Version? rightVersion = TryParse(rightNormalized);

            if (leftVersion is not null && rightVersion is not null)
            {
                int versionCompare = leftVersion.CompareTo(rightVersion);
                if (versionCompare != 0)
                    return versionCompare;

                string leftPrerelease = GetPrereleaseLabel(leftNormalized);
                string rightPrerelease = GetPrereleaseLabel(rightNormalized);
                bool leftIsPrerelease = !string.IsNullOrWhiteSpace(leftPrerelease);
                bool rightIsPrerelease = !string.IsNullOrWhiteSpace(rightPrerelease);

                if (leftIsPrerelease && !rightIsPrerelease)
                    return -1;
                if (!leftIsPrerelease && rightIsPrerelease)
                    return 1;
                if (!leftIsPrerelease && !rightIsPrerelease)
                    return 0;

                return ComparePrereleaseLabels(leftPrerelease, rightPrerelease);
            }

            return string.Compare(leftNormalized, rightNormalized, StringComparison.OrdinalIgnoreCase);
        }

        private static int ComparePrereleaseLabels(string left, string right)
        {
            string[] leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string[] rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int count = Math.Max(leftParts.Length, rightParts.Length);

            for (int i = 0; i < count; i++)
            {
                if (i >= leftParts.Length)
                    return -1;
                if (i >= rightParts.Length)
                    return 1;

                string leftPart = leftParts[i];
                string rightPart = rightParts[i];
                bool leftIsNumber = int.TryParse(leftPart, out int leftNumber);
                bool rightIsNumber = int.TryParse(rightPart, out int rightNumber);

                if (leftIsNumber && rightIsNumber)
                {
                    int numericCompare = leftNumber.CompareTo(rightNumber);
                    if (numericCompare != 0)
                        return numericCompare;

                    continue;
                }

                if (leftIsNumber && !rightIsNumber)
                    return -1;
                if (!leftIsNumber && rightIsNumber)
                    return 1;

                int textCompare = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
                if (textCompare != 0)
                    return textCompare;
            }

            return 0;
        }
    }
}
