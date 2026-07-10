using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class UnifiedTextDiffServiceTests
{
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
    public void Create_TruncatesLargeInputsAndOutput()
    {
        UnifiedTextDiffResult result = UnifiedTextDiffService.Create(
            "one\ntwo\nthree\nfour",
            "one\nchanged\nthree\nfour",
            "a",
            "b",
            new UnifiedTextDiffOptions(MaxLinesPerFile: 2, MaxOutputLines: 5));

        Assert.True(result.IsTruncated);
        Assert.Contains("truncated", result.Text);
    }
}
