using System;
using System.IO;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class StablePresetIndexBackportTests
{
    [Fact]
    public void LowercasePresetIndexPropertiesResolveRequestedPresetRules()
    {
        using var root = new TempDirectory();
        string project = Directory.CreateDirectory(Path.Combine(root.Path, "project")).FullName;
        string presets = Directory.CreateDirectory(Path.Combine(root.Path, "presets")).FullName;
        File.WriteAllText(Path.Combine(presets, "presets.index.json"),
            """{"presets":[{"id":"lowercase-test","file":"mapped.vaultsyncignore"}]}""");
        File.WriteAllText(Path.Combine(presets, "mapped.vaultsyncignore"), "sensitive-file.txt");

        FilterService filter = FilterService.FromPresetAndLocal(project, "lowercase-test", presets);

        Assert.True(filter.ShouldExclude(project, Path.Combine(project, "sensitive-file.txt")));
        Assert.False(filter.ShouldExclude(project, Path.Combine(project, "include-me.txt")));
    }
}
