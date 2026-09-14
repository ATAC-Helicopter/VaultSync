using System;
using System.IO;
using System.Linq;
using VaultSync.CLI.Presets;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PresetStoreTests
{
    [Fact]
    public void ListNamesUsesIndexedIdsAndFileNameFallbacks()
    {
        using var root = new TempDirectory();
        string builtInDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "built-in")).FullName;
        File.WriteAllText(
            Path.Combine(builtInDirectory, "presets.index.json"),
            """
            {"Presets":[
              {"Id":"indexed-name","Name":"Indexed","Category":"Test","File":"physical-name.vaultsyncignore","Description":"Test"},
              {"Id":"","Name":"Fallback","Category":"Test","File":"fallback-name.vaultsyncignore","Description":"Test"}
            ]}
            """);

        string previous = Environment.GetEnvironmentVariable("VAULTSYNC_PRESETS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("VAULTSYNC_PRESETS_DIR", builtInDirectory);

            string[] names = PresetStore.ListNames().ToArray();

            Assert.Contains("indexed-name", names);
            Assert.Contains("fallback-name", names);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAULTSYNC_PRESETS_DIR", previous);
        }
    }

    [Fact]
    public void LoadUsesIndexedFileAndFallsBackWhenIndexIsMalformed()
    {
        using var root = new TempDirectory();
        string builtInDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "built-in")).FullName;
        string indexPath = Path.Combine(builtInDirectory, "presets.index.json");
        File.WriteAllText(indexPath, "{\"Presets\":[{\"Id\":\"friendly\",\"File\":\"stored.vaultsyncignore\"}]}");
        File.WriteAllText(Path.Combine(builtInDirectory, "stored.vaultsyncignore"), "indexed-content");
        File.WriteAllText(Path.Combine(builtInDirectory, "fallback.vaultsyncignore"), "fallback-content");

        string previous = Environment.GetEnvironmentVariable("VAULTSYNC_PRESETS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("VAULTSYNC_PRESETS_DIR", builtInDirectory);

            Assert.Equal("indexed-content", PresetStore.Load("friendly"));

            File.WriteAllText(indexPath, "not-json");
            Assert.Equal("fallback-content", PresetStore.Load("fallback"));
            Assert.Contains("fallback", PresetStore.ListNames());
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAULTSYNC_PRESETS_DIR", previous);
        }
    }

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
