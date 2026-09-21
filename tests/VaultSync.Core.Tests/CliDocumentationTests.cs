using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Spectre.Console;
using VaultSync.CLI;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliDocumentationTests
{
    [Fact]
    public async Task EmptyInvocationShowsCompactIdentityLinksAndCommandHelp()
    {
        IAnsiConsole previous = AnsiConsole.Console;
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            Ansi = AnsiSupport.No
        });
        console.Profile.Width = 120;
        TextWriter previousOut = Console.Out;
        try
        {
            AnsiConsole.Console = console;
            Console.SetOut(output);
            Assert.Equal(0, await Program.Main([]));
        }
        finally
        {
            Console.SetOut(previousOut);
            AnsiConsole.Console = previous;
        }

        string rendered = output.ToString();
        Assert.Contains("VAULTSYNC CLI 1.9.0", rendered, StringComparison.Ordinal);
        Assert.Contains("Snapshot · Back up · Verify · Recover", rendered, StringComparison.Ordinal);
        Assert.Contains("https://fglabs.dev/vaultsync", rendered, StringComparison.Ordinal);
        Assert.Contains("vaultsync docs", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsFullPrintsBundledHandbookWithoutTerminalDecoration()
    {
        (int code, string output, string error) = await CaptureStandardStreams("docs", "--full");
        Assert.Equal(0, code);
        Assert.StartsWith("# VaultSync CLI handbook", output, StringComparison.Ordinal);
        Assert.Contains("## Structured output v1", output, StringComparison.Ordinal);
        Assert.Contains("## Compatibility routes", output, StringComparison.Ordinal);
        Assert.DoesNotContain("╭", output, StringComparison.Ordinal);
        Assert.Empty(error);
    }

    [Fact]
    public async Task DocsUrlIsStableAndPipeFriendly()
    {
        (int code, string output, string error) = await CaptureStandardStreams("docs", "--url");
        Assert.Equal(0, code);
        Assert.Equal("https://github.com/ATAC-Helicopter/VaultSync/blob/release/1.9.0/docs/CLI.md",
            output.Trim());
        Assert.Empty(error);
    }

    [Fact]
    public void CliHandbookIsEmbeddedInTheToolAssembly()
    {
        Assert.Contains("VaultSync.CLI.Docs.CLI.md",
            typeof(Program).Assembly.GetManifestResourceNames(), StringComparer.Ordinal);
    }

    private static async Task<(int Code, string Output, string Error)> CaptureStandardStreams(params string[] args)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int code = await Program.Main(args);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
