using System;
using System.IO;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class FilterServiceCacheRegressionTests
{
    [Fact]
    public void RewrittenPresetIndexWithUnchangedTimestampMustBeReloaded()
    {
        using var root = new TempDirectory();
        string presets = Directory.CreateDirectory(Path.Combine(root.Path, "presets")).FullName;
        string indexPath = Path.Combine(presets, "presets.index.json");
        File.WriteAllText(Path.Combine(presets, "first.vaultsyncignore"), "first.txt\n");
        File.WriteAllText(Path.Combine(presets, "later.vaultsyncignore"), "later.txt\n");
        File.WriteAllText(indexPath, """{"Presets":[{"Id":"example","File":"first.vaultsyncignore"}]}""");
        DateTime timestamp = File.GetLastWriteTimeUtc(indexPath);
        FilterService original = FilterService.FromPresetAndLocal(root.Path, "example", presets);
        Assert.True(original.ShouldExclude(root.Path, Path.Combine(root.Path, "first.txt")));

        File.WriteAllText(indexPath, """{"Presets":[{"Id":"example","File":"later.vaultsyncignore"}]}""");
        File.SetLastWriteTimeUtc(indexPath, timestamp);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(indexPath));
        FilterService updated = FilterService.FromPresetAndLocal(root.Path, "example", presets);
        Assert.True(updated.ShouldExclude(root.Path, Path.Combine(root.Path, "later.txt")));
        Assert.False(updated.ShouldExclude(root.Path, Path.Combine(root.Path, "first.txt")));
    }

    [Fact]
    public void RewrittenProjectIgnoreWithUnchangedTimestampMustBeReloaded()
    {
        using var root = new TempDirectory();
        string ignorePath = Path.Combine(root.Path, ".vaultsyncignore");
        File.WriteAllText(ignorePath, "first.txt\n");
        var timestamp = File.GetLastWriteTimeUtc(ignorePath);
        FilterService initial = FilterService.FromPresetAndLocal(root.Path, "", root.Path);
        Assert.True(initial.ShouldExclude(root.Path, Path.Combine(root.Path, "first.txt")));
        File.WriteAllText(ignorePath, "later.txt\n");
        File.SetLastWriteTimeUtc(ignorePath, timestamp);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(ignorePath));
        FilterService refreshed = FilterService.FromPresetAndLocal(root.Path, "", root.Path);
        Assert.True(refreshed.ShouldExclude(root.Path, Path.Combine(root.Path, "later.txt")));
        Assert.False(refreshed.ShouldExclude(root.Path, Path.Combine(root.Path, "first.txt")));
    }
}
