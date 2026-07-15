using System;
using System.Linq;
using System.Threading;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class UnifiedTextDiffServiceTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 1)]
    public void Create_RejectsNonPositiveWorkBounds(int maxLinesPerFile, int maxOutputLines)
    {
        var options = new UnifiedTextDiffOptions(maxLinesPerFile, maxOutputLines);

        Assert.Throws<ArgumentOutOfRangeException>(() => UnifiedTextDiffService.Create(
            "old",
            "new",
            "a",
            "b",
            options));
    }

    [Fact]
    public void Create_ProducesUnifiedLineChanges()
    {
        UnifiedTextDiffResult result = UnifiedTextDiffService.Create(
            "same\nold\ntail",
            "same\nnew\ntail\nadded",
            "snapshot-a/file.txt",
            "snapshot-b/file.txt");

        Assert.StartsWith("--- snapshot-a/file.txt", result.Text);
        Assert.Contains("-old", result.Text);
        Assert.Contains("+new", result.Text);
        Assert.Contains("+added", result.Text);
        Assert.Equal(2, result.AddedLines);
        Assert.Equal(1, result.DeletedLines);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void Create_UsesCompactGitStyleHunksInsteadOfRenderingTheWholeFile()
    {
        string[] older = Enumerable.Range(1, 30).Select(index => $"line {index}").ToArray();
        string[] newer = [.. older];
        newer[2] = "changed near start";
        newer[25] = "changed near end";

        UnifiedTextDiffResult result = UnifiedTextDiffService.Create(
            string.Join('\n', older),
            string.Join('\n', newer),
            "a/file.txt",
            "b/file.txt",
            new UnifiedTextDiffOptions(ContextLines: 2));

        Assert.Equal(2, result.Text.Split('\n').Count(line => line.StartsWith("@@", StringComparison.Ordinal)));
        Assert.Contains(" line 1", result.Text);
        Assert.Contains(" line 28", result.Text);
        Assert.DoesNotContain(" line 15", result.Text);
    }

    [Fact]
    public void Create_ReportsLineEndingOnlyChangesWithoutEmbeddingUiCopy()
    {
        UnifiedTextDiffResult result = UnifiedTextDiffService.Create(
            "first\r\nsecond\r\n",
            "first\nsecond\n",
            "a/file.txt",
            "b/file.txt");

        Assert.True(result.HasLineEndingChange);
        Assert.Equal("CRLF", result.OlderLineEnding);
        Assert.Equal("LF", result.NewerLineEnding);
        Assert.DoesNotContain("line endings", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.AddedLines);
        Assert.Equal(0, result.DeletedLines);
    }

    [Fact]
    public void Create_TruncatesLargeInputsAndOutput()
    {
        UnifiedTextDiffResult result = UnifiedTextDiffService.Create(
            "one\ntwo\nthree\nfour",
            "one\nchanged\nthree\nfour",
            "a",
            "b",
            new UnifiedTextDiffOptions(MaxLinesPerFile: 2, MaxOutputLines: 5));

        Assert.True(result.IsTruncated);
        Assert.DoesNotContain("truncated", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HonorsCancellationBeforeQuadraticWork()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => UnifiedTextDiffService.Create(
            string.Join('\n', Enumerable.Repeat("old", 800)),
            string.Join('\n', Enumerable.Repeat("new", 800)),
            "a",
            "b",
            cancellationToken: cts.Token));
    }
}
