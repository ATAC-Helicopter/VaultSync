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

        string olderRaw = olderText ?? string.Empty;
        string newerRaw = newerText ?? string.Empty;
        string[] allOlder = SplitLines(olderRaw);
        string[] allNewer = SplitLines(newerRaw);
        bool truncated = false;
        var output = new List<string>(Math.Min(options.MaxOutputLines, allOlder.Length + allNewer.Length + 3))
        {
            $"--- {olderLabel}",
            $"+++ {newerLabel}"
        };

        List<DiffOperation> operations = BuildOperations(
            allOlder,
            allNewer,
            options.MaxLinesPerFile,
            cancellationToken);
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

        return new UnifiedTextDiffResult(
            string.Join(Environment.NewLine, output),
            added,
            deleted,
            truncated);
    }

    private static List<DiffOperation> BuildOperations(
        string[] older,
        string[] newer,
        int exactDiffLineLimit,
        CancellationToken cancellationToken)
    {
        if (older.Length <= exactDiffLineLimit && newer.Length <= exactDiffLineLimit)
        {
            int[,] lcs = BuildLongestCommonSubsequence(older, newer, cancellationToken);
            return BuildExactOperations(older, newer, lcs, cancellationToken);
        }

        return BuildBoundedOperations(older, newer, cancellationToken);
    }

    private static List<DiffOperation> BuildExactOperations(
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

    private static List<DiffOperation> BuildBoundedOperations(
        string[] older,
        string[] newer,
        CancellationToken cancellationToken)
    {
        const int synchronizationLookahead = 96;
        var operations = new List<DiffOperation>(older.Length + newer.Length);
        int oldIndex = 0;
        int newIndex = 0;
        while (oldIndex < older.Length || newIndex < newer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAppendMatchingLine(older, newer, operations, ref oldIndex, ref newIndex))
                continue;

            (int oldSkip, int newSkip) = FindNextSynchronizationPoint(
                older,
                newer,
                oldIndex,
                newIndex,
                synchronizationLookahead);
            if (oldSkip == 0 && newSkip == 0)
            {
                AppendNextUnmatchedLines(older, newer, operations, ref oldIndex, ref newIndex);
                continue;
            }

            AppendSkippedLines(older, operations, '-', oldSkip, ref oldIndex, ref newIndex);
            AppendSkippedLines(newer, operations, '+', newSkip, ref newIndex, ref oldIndex);
        }

        return operations;
    }

    private static bool TryAppendMatchingLine(
        string[] older,
        string[] newer,
        List<DiffOperation> operations,
        ref int oldIndex,
        ref int newIndex)
    {
        if (oldIndex >= older.Length || newIndex >= newer.Length ||
            !string.Equals(older[oldIndex], newer[newIndex], StringComparison.Ordinal))
        {
            return false;
        }

        operations.Add(new DiffOperation(' ', older[oldIndex], oldIndex + 1, newIndex + 1));
        oldIndex++;
        newIndex++;
        return true;
    }

    private static void AppendNextUnmatchedLines(
        string[] older,
        string[] newer,
        List<DiffOperation> operations,
        ref int oldIndex,
        ref int newIndex)
    {
        if (oldIndex < older.Length)
        {
            operations.Add(new DiffOperation('-', older[oldIndex], oldIndex + 1, newIndex + 1));
            oldIndex++;
        }

        if (newIndex < newer.Length)
        {
            operations.Add(new DiffOperation('+', newer[newIndex], oldIndex + 1, newIndex + 1));
            newIndex++;
        }
    }

    private static void AppendSkippedLines(
        string[] lines,
        List<DiffOperation> operations,
        char marker,
        int count,
        ref int advancingIndex,
        ref int otherIndex)
    {
        for (int index = 0; index < count; index++)
        {
            int oldPosition = marker == '-' ? advancingIndex + 1 : otherIndex + 1;
            int newPosition = marker == '+' ? advancingIndex + 1 : otherIndex + 1;
            operations.Add(new DiffOperation(marker, lines[advancingIndex], oldPosition, newPosition));
            advancingIndex++;
        }
    }

    private static (int OldSkip, int NewSkip) FindNextSynchronizationPoint(
        string[] older,
        string[] newer,
        int oldIndex,
        int newIndex,
        int maxLookahead)
    {
        int maxOldSkip = Math.Min(maxLookahead, older.Length - oldIndex);
        int maxNewSkip = Math.Min(maxLookahead, newer.Length - newIndex);
        int maxDistance = Math.Min(maxLookahead, maxOldSkip + maxNewSkip);
        for (int distance = 1; distance <= maxDistance; distance++)
        {
            int firstOldSkip = Math.Max(0, distance - maxNewSkip);
            int lastOldSkip = Math.Min(distance, maxOldSkip);
            for (int oldSkip = firstOldSkip; oldSkip <= lastOldSkip; oldSkip++)
            {
                int newSkip = distance - oldSkip;
                if (oldIndex + oldSkip >= older.Length || newIndex + newSkip >= newer.Length)
                    continue;
                if (string.Equals(older[oldIndex + oldSkip], newer[newIndex + newSkip], StringComparison.Ordinal))
                    return (oldSkip, newSkip);
            }
        }

        return (0, 0);
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
