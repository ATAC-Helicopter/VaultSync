#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Spectre.Console;
using VaultSync.CLI;
using VaultSync.CLI.Commands;
using Xunit;

namespace VaultSync.Core.Tests;

[CollectionDefinition("CLI presentation console", DisableParallelization = true)]
public sealed class CliPresentationConsoleCollection;

[Collection("CLI presentation console")]
public sealed class CliDocumentationTests
{
    [Fact]
    public async Task EmptyInvocationShowsIntroductoryUiAndBasicCommands()
    {
        (int code, string rendered) = await CaptureAnsi(120);
        Assert.Equal(0, code);
        Assert.Contains("VAULTSYNC CLI 1.9.0", rendered, StringComparison.Ordinal);
        Assert.Contains("Snapshot · Back up · Verify · Recover", rendered, StringComparison.Ordinal);
        Assert.Contains("https://fglabs.dev/vaultsync", rendered, StringComparison.Ordinal);
        Assert.Contains("vaultsync docs", rendered, StringComparison.Ordinal);
        Assert.Contains("vaultsync projects add NAME PATH", rendered, StringComparison.Ordinal);
        Assert.Contains("https://github.com/ATAC-Helicopter/VaultSync", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("USAGE:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootHelpKeepsTheCommandReferenceSeparateFromTheLandingPage()
    {
        (int code, string rendered) = await CaptureAnsi(120, "--help");
        Assert.Equal(0, code);
        Assert.Contains("VAULTSYNC CLI 1.9.0", rendered, StringComparison.Ordinal);
        Assert.Contains("USAGE:", rendered, StringComparison.Ordinal);
        Assert.Contains("COMMANDS:", rendered, StringComparison.Ordinal);
        Assert.Contains("vaultsync docs", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsWithoutModeShowsTheDocumentationMenu()
    {
        (int code, string rendered) = await CaptureAnsi(120, "docs");
        Assert.Equal(0, code);
        Assert.Contains(CliPresentation.DocumentationUrl, rendered, StringComparison.Ordinal);
        Assert.Contains("vaultsync docs --full", rendered, StringComparison.Ordinal);
        Assert.Contains("vaultsync docs --open", rendered, StringComparison.Ordinal);
        Assert.Contains("vaultsync docs --url", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NarrowTerminalUsesTheCompactLandingTitle()
    {
        (int code, string rendered) = await CaptureAnsi(60);
        Assert.Equal(0, code);
        Assert.Contains("VaultSync", rendered, StringComparison.Ordinal);
        Assert.Contains("Start here", rendered, StringComparison.Ordinal);
        Assert.Contains("vaultsync snapshots", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("__     __", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsFullPrintsBundledHandbookWithoutTerminalDecoration()
    {
        (int code, string output, string error) = await CaptureStandardStreams("docs", "--full");
        Assert.Equal(0, code);
        Assert.StartsWith("# VaultSync CLI handbook", output, StringComparison.Ordinal);
        Assert.Contains("## Structured output v1", output, StringComparison.Ordinal);
        Assert.Contains("## Compatibility routes", output, StringComparison.Ordinal);
        Assert.Contains("# VaultSync CLI task guides", output, StringComparison.Ordinal);
        Assert.Contains("## automate", output, StringComparison.Ordinal);
        Assert.Contains("# VaultSync CLI generated command reference", output, StringComparison.Ordinal);
        Assert.Contains("## `vaultsync recovery restore`", output, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("setup", "vaultsync --version --json")]
    [InlineData("inspect", "mktemp -d")]
    [InlineData("mirror", "--dry-run")]
    [InlineData("restore", "recorded folder backup")]
    [InlineData("automate", "systemd user timer")]
    [InlineData("migrate", "--output json")]
    public async Task DocsTaskPrintsOnlyTheRequestedGuide(string topic, string marker)
    {
        (int code, string output, string error) = await CaptureStandardStreams("docs", "--task", topic);
        Assert.Equal(0, code);
        Assert.StartsWith($"## {topic}", output, StringComparison.Ordinal);
        Assert.Contains(marker, output, StringComparison.Ordinal);
        Assert.DoesNotContain("# VaultSync CLI generated command reference", output, StringComparison.Ordinal);
        Assert.Empty(error);
    }

    [Fact]
    public async Task DocsTaskRejectsUnknownTopicWithoutWritingToStdout()
    {
        (int code, string output, string error) = await CaptureStandardStreams("docs", "--task", "unknown");
        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains("Choose: setup, inspect, mirror, restore, automate, migrate", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CliHandbookIsEmbeddedInTheToolAssembly()
    {
        string[] resources = typeof(Program).Assembly.GetManifestResourceNames();
        Assert.Contains("VaultSync.CLI.Docs.CLI.md", resources, StringComparer.Ordinal);
        Assert.Contains("VaultSync.CLI.Docs.CLI_TASK_GUIDES.md", resources, StringComparer.Ordinal);
        Assert.Contains("VaultSync.CLI.Docs.CLI_COMMAND_REFERENCE.md", resources, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task DocsOpenReportsBrowserLaunchOutcome(bool opens, int expectedExit)
    {
        Func<string, bool> previous = DocsCommand.BrowserLauncher;
        string? requested = null;
        try
        {
            DocsCommand.BrowserLauncher = url => { requested = url; return opens; };
            (int code, string output, string error) = await CaptureStandardStreams("docs", "--open");
            Assert.Equal(expectedExit, code);
            Assert.Equal(CliPresentation.DocumentationUrl, requested);
            if (opens)
            {
                Assert.Contains("Opened", output, StringComparison.Ordinal);
                Assert.Empty(error);
            }
            else
            {
                Assert.Empty(output);
                Assert.Contains(CliPresentation.DocumentationUrl, error, StringComparison.Ordinal);
            }
        }
        finally
        {
            DocsCommand.BrowserLauncher = previous;
        }
    }

    [Theory]
    [InlineData("bash", "complete -F _vaultsync_complete vaultsync")]
    [InlineData("zsh", "#compdef vaultsync")]
    [InlineData("powershell", "Register-ArgumentCompleter -Native -CommandName vaultsync")]
    [InlineData("pwsh", "Register-ArgumentCompleter -Native -CommandName vaultsync")]
    public async Task CompletionPrintsBundledShellScript(string shell, string marker)
    {
        (int code, string output, string error) = await CaptureStandardStreams("completion", shell);
        Assert.Equal(0, code);
        Assert.StartsWith("#", output, StringComparison.Ordinal);
        Assert.Contains(marker, output, StringComparison.Ordinal);
        Assert.Contains("projects", output, StringComparison.Ordinal);
        Assert.Contains("snapshots", output, StringComparison.Ordinal);
        Assert.Contains("--output", output, StringComparison.Ordinal);
        Assert.Empty(error);
    }

    [Fact]
    public async Task CompletionRejectsUnsupportedShellWithoutPrintingAScript()
    {
        (int code, string output, string error) = await CaptureStandardStreams("completion", "fish");
        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains("Choose bash, zsh, or powershell", error, StringComparison.Ordinal);
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

    private static async Task<(int Code, string Output)> CaptureAnsi(int width, params string[] args)
    {
        IAnsiConsole previousConsole = AnsiConsole.Console;
        TextWriter previousOut = Console.Out;
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            Ansi = AnsiSupport.No
        });
        console.Profile.Width = width;

        try
        {
            AnsiConsole.Console = console;
            Console.SetOut(output);
            int code = await Program.Main(args);
            return (code, output.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            AnsiConsole.Console = previousConsole;
        }
    }
}
