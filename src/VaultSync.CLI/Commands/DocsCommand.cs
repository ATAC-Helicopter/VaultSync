using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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

    public static bool IsRootHelp(string[] arguments) => arguments.Length == 1 &&
        (arguments[0] == "--help" || arguments[0] == "-h");

    public static void WriteLanding()
    {
        if (AnsiConsole.Profile.Width >= 72)
            AnsiConsole.Write(new FigletText("VaultSync").Centered().Color(Color.DeepSkyBlue1));
        else
            AnsiConsole.Write(new Rule("[bold deepskyblue1]VaultSync[/]").RuleStyle("deepskyblue1"));

        WritePurposePanel();
        WriteLinks();

        AnsiConsole.MarkupLine("[bold]Start here[/]");
        var commands = new Table().Border(TableBorder.Simple).HideHeaders();
        commands.AddColumn("Command");
        commands.AddColumn("Purpose");
        commands.AddRow("[cyan]vaultsync projects list[/]", "Inspect registered projects");
        commands.AddRow("[cyan]vaultsync projects add NAME PATH[/]", "Register a source folder");
        commands.AddRow("[cyan]vaultsync snapshots create NAME[/]", "Index and hash current source state");
        commands.AddRow("[cyan]vaultsync snapshots list NAME[/]", "Inspect snapshot history");
        commands.AddRow("[cyan]vaultsync mirror NAME DEST --dry-run[/]", "Preview a live mirror operation");
        commands.AddRow("[cyan]vaultsync docs[/]", "Open documentation choices");
        AnsiConsole.Write(commands);
        AnsiConsole.MarkupLine("[grey]Every command:[/] vaultsync --help   [grey]One command:[/] vaultsync COMMAND --help");
    }

    public static void WriteHelpHeader()
    {
        WritePurposePanel();
        WriteLinks();
        AnsiConsole.MarkupLine("[grey]Handbook:[/] vaultsync docs");
        AnsiConsole.WriteLine();
    }

    private static void WritePurposePanel()
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
    }

    private static void WriteLinks()
    {
        AnsiConsole.MarkupLine(
            $"[grey]Docs:[/] [link={DocumentationUrl}]{DocumentationUrl}[/]\n" +
            $"[grey]Website:[/] [link={WebsiteUrl}]{WebsiteUrl}[/]\n" +
            $"[grey]Repository:[/] [link={SourceUrl}]{SourceUrl}[/]\n" +
            $"[grey]Releases:[/] [link={ReleasesUrl}]{ReleasesUrl}[/]");
        AnsiConsole.WriteLine();
    }

    public static void WriteDocumentationIntro()
    {
        WriteHelpHeader();
        var table = new Table().Border(TableBorder.Simple).HideHeaders();
        table.AddColumn("Command");
        table.AddColumn("Purpose");
        table.AddRow("[cyan]vaultsync docs --full[/]", "Print the complete bundled Markdown handbook");
        table.AddRow("[cyan]vaultsync docs --task inspect[/]", "Read a focused setup, inspection, mirror, restore, automation, or migration guide");
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
    [CommandOption("--task <TOPIC>")] public string? Task { get; init; }

    public override ValidationResult Validate() => new[] { Full, Open, Url, Task is not null }.Count(value => value) > 1
        ? ValidationResult.Error("Choose only one of --full, --open, --url, or --task.")
        : ValidationResult.Success();
}

internal sealed class DocsCommand : AsyncCommand<DocsSettings>
{
    private const string HandbookResource = "VaultSync.CLI.Docs.CLI.md";
    private const string ReferenceResource = "VaultSync.CLI.Docs.CLI_COMMAND_REFERENCE.md";
    private const string TasksResource = "VaultSync.CLI.Docs.CLI_TASK_GUIDES.md";
    private static readonly string[] Topics = ["setup", "inspect", "mirror", "restore", "automate", "migrate"];
    internal static Func<string, bool> BrowserLauncher { get; set; } = LaunchBrowser;

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
            string? handbook = ReadResource(HandbookResource);
            string? tasks = ReadResource(TasksResource);
            string? reference = ReadResource(ReferenceResource);
            if (handbook is null || tasks is null || reference is null)
            {
                Console.Error.WriteLine("The bundled CLI handbook is unavailable. Use `vaultsync docs --url` for the online documentation.");
                return Task.FromResult(1);
            }
            Console.Write(handbook.TrimEnd());
            Console.Write("\n\n---\n\n");
            Console.Write(tasks.TrimEnd());
            Console.Write("\n\n---\n\n");
            Console.Write(reference);
            return Task.FromResult(0);
        }
        if (settings.Task is not null)
        {
            string topic = settings.Task.Trim().ToLowerInvariant();
            if (!Topics.Contains(topic, StringComparer.Ordinal))
            {
                Console.Error.WriteLine($"Unknown task '{settings.Task}'. Choose: {string.Join(", ", Topics)}.");
                return Task.FromResult(2);
            }
            string? guides = ReadResource(TasksResource);
            string? guide = guides is null ? null : FindTask(guides, topic);
            if (guide is null)
            {
                Console.Error.WriteLine("The bundled CLI task guide is unavailable. Use `vaultsync docs --url` for online documentation.");
                return Task.FromResult(1);
            }
            Console.Write(guide);
            return Task.FromResult(0);
        }
        if (settings.Open)
        {
            if (BrowserLauncher(CliPresentation.DocumentationUrl))
            {
                Console.WriteLine("Opened the VaultSync CLI handbook in the default browser.");
                return Task.FromResult(0);
            }
            Console.Error.WriteLine($"Could not open the browser. Documentation: {CliPresentation.DocumentationUrl}");
            return Task.FromResult(1);
        }

        CliPresentation.WriteDocumentationIntro();
        return Task.FromResult(0);
    }

    private static string? ReadResource(string name)
    {
        using Stream? stream = typeof(DocsCommand).Assembly.GetManifestResourceStream(name);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? FindTask(string markdown, string topic)
    {
        using var reader = new StringReader(markdown);
        var section = new StringBuilder();
        bool found = false;
        while (reader.ReadLine() is string line)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (found)
                    break;
                found = string.Equals(line[3..].Trim(), topic, StringComparison.Ordinal);
            }
            if (found)
                section.AppendLine(line);
        }
        return found ? section.ToString().TrimEnd() + Environment.NewLine : null;
    }

    private static bool LaunchBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or
            System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
