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

            int? emptyComparison = CompareEmptyIdentities(leftNormalized, rightNormalized);
            if (emptyComparison.HasValue)
                return emptyComparison.Value;

            Version? leftVersion = TryParse(leftNormalized);
            Version? rightVersion = TryParse(rightNormalized);
            if (leftVersion is null || rightVersion is null)
                return string.Compare(leftNormalized, rightNormalized, StringComparison.OrdinalIgnoreCase);

            int versionComparison = leftVersion.CompareTo(rightVersion);
            return versionComparison != 0
                ? versionComparison
                : ComparePrereleaseIdentities(leftNormalized, rightNormalized);
        }

        private static int? CompareEmptyIdentities(string left, string right)
        {
            bool leftIsEmpty = string.IsNullOrWhiteSpace(left);
            bool rightIsEmpty = string.IsNullOrWhiteSpace(right);
            if (!leftIsEmpty && !rightIsEmpty)
                return null;
            if (leftIsEmpty == rightIsEmpty)
                return 0;

            return leftIsEmpty ? -1 : 1;
        }

        private static int ComparePrereleaseIdentities(string left, string right)
        {
            string leftPrerelease = GetPrereleaseLabel(left);
            string rightPrerelease = GetPrereleaseLabel(right);
            bool leftIsPrerelease = !string.IsNullOrWhiteSpace(leftPrerelease);
            bool rightIsPrerelease = !string.IsNullOrWhiteSpace(rightPrerelease);

            if (leftIsPrerelease != rightIsPrerelease)
                return leftIsPrerelease ? -1 : 1;

            return leftIsPrerelease
                ? ComparePrereleaseLabels(leftPrerelease, rightPrerelease)
                : 0;
        }

        private static int ComparePrereleaseLabels(string left, string right)
        {
            string[] leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string[] rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int sharedCount = Math.Min(leftParts.Length, rightParts.Length);

            for (int i = 0; i < sharedCount; i++)
            {
                string leftPart = leftParts[i];
                string rightPart = rightParts[i];
                bool leftIsNumber = int.TryParse(leftPart, out int leftNumber);
                bool rightIsNumber = int.TryParse(rightPart, out int rightNumber);

                int partComparison;
                if (leftIsNumber != rightIsNumber)
                {
                    partComparison = leftIsNumber ? -1 : 1;
                }
                else
                {
                    partComparison = leftIsNumber
                        ? leftNumber.CompareTo(rightNumber)
                        : string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
                }

                if (partComparison != 0)
                    return partComparison;
            }

            return leftParts.Length.CompareTo(rightParts.Length);
        }
    }
}
