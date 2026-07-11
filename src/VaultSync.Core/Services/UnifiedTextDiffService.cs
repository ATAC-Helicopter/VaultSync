namespace VaultSync.Core.Services;

public sealed record UnifiedTextDiffOptions(
    int MaxLinesPerFile = 800,
    int MaxOutputLines = 2_000)
{
    public static UnifiedTextDiffOptions Default { get; } = new();
}

public sealed record UnifiedTextDiffResult(
    string Text,
    int AddedLines,
    int DeletedLines,
    bool IsTruncated);

/// <summary>
/// Creates a bounded, line-oriented unified diff suitable for snapshot previews.
/// The bounds keep accidental large/generated files from blocking the UI.
/// </summary>
public static class UnifiedTextDiffService
{
    public static UnifiedTextDiffResult Create(
        string? olderText,
        string? newerText,
        string olderLabel,
        string newerLabel,
        UnifiedTextDiffOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= UnifiedTextDiffOptions.Default;
        if (options.MaxLinesPerFile <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The line limit must be positive.");
        if (options.MaxOutputLines < 2)
            throw new ArgumentOutOfRangeException(nameof(options), "The output limit must allow the diff header.");

        string[] allOlder = SplitLines(olderText);
        string[] allNewer = SplitLines(newerText);
        bool truncated = allOlder.Length > options.MaxLinesPerFile || allNewer.Length > options.MaxLinesPerFile;
        string[] older = [.. allOlder.Take(options.MaxLinesPerFile)];
        string[] newer = [.. allNewer.Take(options.MaxLinesPerFile)];

        int[,] lcs = BuildLongestCommonSubsequence(older, newer, cancellationToken);
        var output = new List<string>(Math.Min(options.MaxOutputLines, older.Length + newer.Length + 3))
        {
            $"--- {olderLabel}",
            $"+++ {newerLabel}",
            $"@@ -1,{older.Length} +1,{newer.Length} @@"
        };

        int oldIndex = 0;
        int newIndex = 0;
        int added = 0;
        int deleted = 0;
        while (oldIndex < older.Length || newIndex < newer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Count >= options.MaxOutputLines)
            {
                truncated = true;
                break;
            }

            if (oldIndex < older.Length && newIndex < newer.Length &&
                string.Equals(older[oldIndex], newer[newIndex], StringComparison.Ordinal))
            {
                output.Add($" {older[oldIndex]}");
                oldIndex++;
                newIndex++;
            }
            else if (newIndex < newer.Length &&
                     (oldIndex >= older.Length || lcs[oldIndex, newIndex + 1] >= lcs[oldIndex + 1, newIndex]))
            {
                output.Add($"+{newer[newIndex++]}");
                added++;
            }
            else
            {
                output.Add($"-{older[oldIndex++]}");
                deleted++;
            }
        }

        if (truncated)
            output.Add("... diff preview truncated by VaultSync safety limits ...");

        return new UnifiedTextDiffResult(string.Join(Environment.NewLine, output), added, deleted, truncated);
    }

    private static string[] SplitLines(string? text) => string.IsNullOrEmpty(text)
        ? []
        : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static int[,] BuildLongestCommonSubsequence(
        string[] older,
        string[] newer,
        CancellationToken cancellationToken)
    {
        var lengths = new int[older.Length + 1, newer.Length + 1];
        for (int oldIndex = older.Length - 1; oldIndex >= 0; oldIndex--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int newIndex = newer.Length - 1; newIndex >= 0; newIndex--)
            {
                lengths[oldIndex, newIndex] = string.Equals(older[oldIndex], newer[newIndex], StringComparison.Ordinal)
                    ? lengths[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(lengths[oldIndex + 1, newIndex], lengths[oldIndex, newIndex + 1]);
            }
        }

        return lengths;
    }
}
