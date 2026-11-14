using System;
using System.IO;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class StableFilterPolicyRefreshRegressionTests
{
    [Fact]
    public void LocalIgnoreChangeWithPreservedTimestampIsAppliedToNewFilter()
    {
        using var root = new TempDirectory();
        string ignore = Path.Combine(root.Path, ".vaultsyncignore");
        File.WriteAllText(ignore, "first.txt\n");
        DateTime stamp = File.GetLastWriteTimeUtc(ignore);
        FilterService first = FilterService.FromPresetAndLocal(root.Path, "", root.Path);
        Assert.True(first.ShouldExclude(root.Path, Path.Combine(root.Path, "first.txt")));

        File.WriteAllText(ignore, "second.txt\n");
        File.SetLastWriteTimeUtc(ignore, stamp);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(ignore));
        FilterService current = FilterService.FromPresetAndLocal(root.Path, "", root.Path);
        Assert.True(current.ShouldExclude(root.Path, Path.Combine(root.Path, "second.txt")));
        Assert.False(current.ShouldExclude(root.Path, Path.Combine(root.Path, "first.txt")));
    }

    [Fact]
    public void PresetFileChangeWithPreservedTimestampIsAppliedToNewFilter()
    {
        using var root = new TempDirectory();
        string presets = Directory.CreateDirectory(Path.Combine(root.Path, "presets")).FullName;
        string ruleFile = Path.Combine(presets, "example.vaultsyncignore");
        File.WriteAllText(ruleFile, "first.txt\n");
        DateTime stamp = File.GetLastWriteTimeUtc(ruleFile);
        FilterService first = FilterService.FromPresetAndLocal(root.Path, "example", presets);
        Assert.True(first.ShouldExclude(root.Path, Path.Combine(root.Path, "first.txt")));

        File.WriteAllText(ruleFile, "second.txt\n");
        File.SetLastWriteTimeUtc(ruleFile, stamp);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(ruleFile));
        FilterService current = FilterService.FromPresetAndLocal(root.Path, "example", presets);
        Assert.True(current.ShouldExclude(root.Path, Path.Combine(root.Path, "second.txt")));
        Assert.False(current.ShouldExclude(root.Path, Path.Combine(root.Path, "first.txt")));
    }

    [Fact]
    public void PresetIndexChangeWithPreservedTimestampIsAppliedToNewFilter()
    {
        using var root = new TempDirectory();
        string presets = Directory.CreateDirectory(Path.Combine(root.Path, "presets")).FullName;
        string index = Path.Combine(presets, "presets.index.json");
        File.WriteAllText(Path.Combine(presets, "first.vaultsyncignore"), "first.txt\n");
        File.WriteAllText(Path.Combine(presets, "second.vaultsyncignore"), "second.txt\n");
        File.WriteAllText(index, """{"Presets":[{"Id":"mapped","File":"first.vaultsyncignore"}]}""");
        DateTime stamp = File.GetLastWriteTimeUtc(index);
        FilterService first = FilterService.FromPresetAndLocal(root.Path, "mapped", presets);
        Assert.True(first.ShouldExclude(root.Path, Path.Combine(root.Path, "first.txt")));

        File.WriteAllText(index, """{"Presets":[{"Id":"mapped","File":"second.vaultsyncignore"}]}""");
        File.SetLastWriteTimeUtc(index, stamp);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(index));
        FilterService current = FilterService.FromPresetAndLocal(root.Path, "mapped", presets);
        Assert.True(current.ShouldExclude(root.Path, Path.Combine(root.Path, "second.txt")));
        Assert.False(current.ShouldExclude(root.Path, Path.Combine(root.Path, "first.txt")));
    }

    [Fact]
    public void LegacyMissingCustomPresetStillAllowsLocalRules()
    {
        using var root = new TempDirectory();
        string presets = Directory.CreateDirectory(Path.Combine(root.Path, "presets")).FullName;
        File.WriteAllText(Path.Combine(root.Path, ".vaultsyncignore"), "local.txt\n");
        FilterService filter = FilterService.FromPresetAndLocal(root.Path, "custom", presets);
        Assert.True(filter.ShouldExclude(root.Path, Path.Combine(root.Path, "local.txt")));
        Assert.False(filter.ShouldExclude(root.Path, Path.Combine(root.Path, "allowed.txt")));
    }
}
