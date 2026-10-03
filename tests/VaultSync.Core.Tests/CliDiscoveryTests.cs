using System;
using System.IO;
using System.Threading.Tasks;
using VaultSync.CLI;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliDiscoveryTests
{
    [Theory]
    [InlineData("--full", "# VaultSync CLI handbook — 1.9.1")]
    [InlineData("--url", "/release/1.9.1/docs/CLI.md")]
    public async Task BundledDocumentationMatchesThisBranch(string mode, string marker)
    {
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, await Program.Main(["docs", mode]));
        }
        finally { Console.SetOut(previous); }
        Assert.Contains(marker, output.ToString());
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("powershell")]
    public async Task PackagedCompletionSuggestsAvailableCommandsOnly(string shell)
    {
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, await Program.Main(["completion", shell]));
        }
        finally { Console.SetOut(previous); }
        Assert.Contains("add-project", output.ToString());
        Assert.Contains("--dry-run", output.ToString());
        Assert.DoesNotContain("backups", output.ToString());
        Assert.DoesNotContain("--output", output.ToString());
    }
}
