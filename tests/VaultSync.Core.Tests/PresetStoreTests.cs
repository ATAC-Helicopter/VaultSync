using System;
using System.IO;
using VaultSync.CLI.Presets;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PresetStoreTests
{
    [Theory]
    [InlineData("../secret")]
    [InlineData("..\\secret")]
    [InlineData("folder/preset")]
    [InlineData("folder\\preset")]
    public void LoadRejectsPathComponents(string name)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => PresetStore.Load(name));

        Assert.Contains("without path components", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadPresetRejectsTraversalOutsidePresetRoot()
    {
        using var root = new TempDirectory();
        string presetDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "presets")).FullName;
        File.WriteAllText(Path.Combine(root.Path, "outside.vaultsyncignore"), "secret");

        bool loaded = PresetStore.TryReadPreset(
            presetDirectory,
            "../outside.vaultsyncignore",
            out string content);

        Assert.False(loaded);
        Assert.Empty(content);
    }

    [Fact]
    public void TryReadPresetLoadsContainedRegularFile()
    {
        using var root = new TempDirectory();
        string presetDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "presets")).FullName;
        File.WriteAllText(Path.Combine(presetDirectory, "safe.vaultsyncignore"), "bin/");

        bool loaded = PresetStore.TryReadPreset(
            presetDirectory,
            "safe.vaultsyncignore",
            out string content);

        Assert.True(loaded);
        Assert.Equal("bin/", content);
    }
}
