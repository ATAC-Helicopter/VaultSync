namespace VaultSync.Core.Services;

public sealed record UnifiedTextDiffOptions(
    int MaxLinesPerFile = 800,
    int MaxOutputLines = 2_000,
    int ContextLines = 3)
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
    private sealed record DiffOperation(
        char Marker,
        string Text,
        int OldPosition,
        int NewPosition);

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
        if (options.ContextLines < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The context line count cannot be negative.");

        string[] allOlder = SplitLines(olderText);
        string[] allNewer = SplitLines(newerText);
        bool truncated = allOlder.Length > options.MaxLinesPerFile || allNewer.Length > options.MaxLinesPerFile;
        string[] older = [.. allOlder.Take(options.MaxLinesPerFile)];
        string[] newer = [.. allNewer.Take(options.MaxLinesPerFile)];

        int[,] lcs = BuildLongestCommonSubsequence(older, newer, cancellationToken);
        var output = new List<string>(Math.Min(options.MaxOutputLines, older.Length + newer.Length + 3))
        {
            $"--- {olderLabel}",
            $"+++ {newerLabel}"
        };

        List<DiffOperation> operations = BuildOperations(older, newer, lcs, cancellationToken);
        int added = operations.Count(operation => operation.Marker == '+');
        int deleted = operations.Count(operation => operation.Marker == '-');
        IReadOnlyList<(int Start, int End)> hunks = BuildHunkRanges(operations, options.ContextLines);

        foreach ((int start, int end) in hunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Count >= options.MaxOutputLines)
            {
                truncated = true;
                break;
            }

            DiffOperation first = operations[start];
            int oldCount = 0;
            int newCount = 0;
            for (int index = start; index <= end; index++)
            {
                oldCount += operations[index].Marker == '+' ? 0 : 1;
                newCount += operations[index].Marker == '-' ? 0 : 1;
            }

            output.Add($"@@ -{first.OldPosition},{oldCount} +{first.NewPosition},{newCount} @@");
            for (int index = start; index <= end; index++)
            {
                if (output.Count >= options.MaxOutputLines)
                {
                    truncated = true;
                    break;
                }

                DiffOperation operation = operations[index];
                output.Add($"{operation.Marker}{operation.Text}");
            }

            if (truncated)
                break;
        }

        if (truncated)
            AppendTruncationNotice(output, options.MaxOutputLines);

        return new UnifiedTextDiffResult(string.Join(Environment.NewLine, output), added, deleted, truncated);
    }

    private static List<DiffOperation> BuildOperations(
        string[] older,
        string[] newer,
        int[,] lcs,
        CancellationToken cancellationToken)
    {
        var operations = new List<DiffOperation>(older.Length + newer.Length);
        int oldIndex = 0;
        int newIndex = 0;
        while (oldIndex < older.Length || newIndex < newer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (oldIndex < older.Length && newIndex < newer.Length &&
                string.Equals(older[oldIndex], newer[newIndex], StringComparison.Ordinal))
            {
                operations.Add(new DiffOperation(' ', older[oldIndex], oldIndex + 1, newIndex + 1));
                oldIndex++;
                newIndex++;
            }
            else if (newIndex < newer.Length &&
                     (oldIndex >= older.Length || lcs[oldIndex, newIndex + 1] > lcs[oldIndex + 1, newIndex]))
            {
                operations.Add(new DiffOperation('+', newer[newIndex], oldIndex + 1, newIndex + 1));
                newIndex++;
            }
            else
            {
                operations.Add(new DiffOperation('-', older[oldIndex], oldIndex + 1, newIndex + 1));
                oldIndex++;
            }
        }

        return operations;
    }

    private static IReadOnlyList<(int Start, int End)> BuildHunkRanges(
        IReadOnlyList<DiffOperation> operations,
        int contextLines)
    {
        var ranges = new List<(int Start, int End)>();
        for (int index = 0; index < operations.Count; index++)
        {
            if (operations[index].Marker == ' ')
                continue;

            int start = Math.Max(0, index - contextLines);
            int end = Math.Min(operations.Count - 1, index + contextLines);
            if (ranges.Count == 0 || start > ranges[^1].End + 1)
            {
                ranges.Add((start, end));
                continue;
            }

            (int previousStart, int previousEnd) = ranges[^1];
            ranges[^1] = (previousStart, Math.Max(previousEnd, end));
        }

        return ranges;
    }

    private static void AppendTruncationNotice(List<string> output, int maxOutputLines)
    {
        const string notice = "... diff preview truncated by VaultSync safety limits ...";
        if (output.Count < maxOutputLines)
            output.Add(notice);
        else if (output.Count > 0)
            output[^1] = notice;
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
