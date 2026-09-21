using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.Core.Services;

namespace VaultSync.CLI.Commands;

internal static class CliPresentation
{
    public const string WebsiteUrl = "https://fglabs.dev/vaultsync";
    public const string SourceUrl = "https://github.com/ATAC-Helicopter/VaultSync";
    public const string ReleasesUrl = "https://github.com/ATAC-Helicopter/VaultSync/releases/latest";
    public const string DocumentationUrl = "https://github.com/ATAC-Helicopter/VaultSync/blob/release/1.9.0/docs/CLI.md";

    public static bool ShouldWriteWelcome(string[] arguments) => arguments.Length == 0 ||
        (arguments.Length == 1 && (arguments[0] == "--help" || arguments[0] == "-h"));

    public static void WriteWelcome()
    {
        BuildInformation build = BuildInformationService.Create(typeof(CliPresentation).Assembly);
        var panel = new Panel(new Markup(
            "[bold]Snapshot · Back up · Verify · Recover[/]\n" +
            "[grey]Power-user tools for inspecting project history and running safe recovery workflows.[/]"))
        {
            Header = new PanelHeader($"[bold deepskyblue1] VAULTSYNC CLI {Markup.Escape(build.Version)} [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.DeepSkyBlue1),
            Padding = new Padding(1, 0, 1, 0)
        };
        AnsiConsole.Write(panel);
        AnsiConsole.MarkupLine(
            $"[grey]Docs:[/] [link={DocumentationUrl}]{DocumentationUrl}[/]\n" +
            $"[grey]Website:[/] [link={WebsiteUrl}]{WebsiteUrl}[/]\n" +
            $"[grey]Source:[/] [link={SourceUrl}]{SourceUrl}[/]\n" +
            "[grey]Start:[/] vaultsync projects list   [grey]Handbook:[/] vaultsync docs");
        AnsiConsole.WriteLine();
    }

    public static void WriteDocumentationIntro()
    {
        WriteWelcome();
        var table = new Table().Border(TableBorder.Simple).HideHeaders();
        table.AddColumn("Command");
        table.AddColumn("Purpose");
        table.AddRow("[cyan]vaultsync docs --full[/]", "Print the complete bundled Markdown handbook");
        table.AddRow("[cyan]vaultsync docs --open[/]", "Open the full online handbook in the default browser");
        table.AddRow("[cyan]vaultsync docs --url[/]", "Print only the canonical documentation URL");
        table.AddRow("[cyan]vaultsync COMMAND --help[/]", "Show command-specific options and arguments");
        AnsiConsole.Write(table);
    }
}

internal sealed class DocsSettings : CommandSettings
{
    [CommandOption("--full")] public bool Full { get; init; }
    [CommandOption("--open")] public bool Open { get; init; }
    [CommandOption("--url")] public bool Url { get; init; }

    public override ValidationResult Validate() => new[] { Full, Open, Url }.Count(value => value) > 1
        ? ValidationResult.Error("Choose only one of --full, --open, or --url.")
        : ValidationResult.Success();
}

internal sealed class DocsCommand : AsyncCommand<DocsSettings>
{
    private const string HandbookResource = "VaultSync.CLI.Docs.CLI.md";

    protected override Task<int> ExecuteAsync(CommandContext context, DocsSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.Url)
        {
            Console.WriteLine(CliPresentation.DocumentationUrl);
            return Task.FromResult(0);
        }
        if (settings.Full)
        {
            using Stream? stream = typeof(DocsCommand).Assembly.GetManifestResourceStream(HandbookResource);
            if (stream is null)
            {
                Console.Error.WriteLine("The bundled CLI handbook is unavailable. Use `vaultsync docs --url` for the online documentation.");
                return Task.FromResult(1);
            }
            using var reader = new StreamReader(stream);
            Console.Write(reader.ReadToEnd());
            return Task.FromResult(0);
        }
        if (settings.Open)
        {
            try
            {
                Process.Start(new ProcessStartInfo(CliPresentation.DocumentationUrl) { UseShellExecute = true });
                Console.WriteLine("Opened the VaultSync CLI handbook in the default browser.");
                return Task.FromResult(0);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"Could not open the browser. Documentation: {CliPresentation.DocumentationUrl}");
                return Task.FromResult(1);
            }
        }

        CliPresentation.WriteDocumentationIntro();
        return Task.FromResult(0);
    }
}
