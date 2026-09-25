using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.CLI.Commands;

internal sealed class ListProjectsSettings : CommandSettings
{
    [CommandOption("--db <PATH>")] public string? Db { get; init; }
    [CommandOption("--json")] public bool Json { get; init; }
    [CommandOption("--output <FORMAT>")] public string? Output { get; init; }
    [CommandOption("--filter <TEXT>")] public string? Filter { get; init; }
    [CommandOption("--preset <PRESET>")] public string? Preset { get; init; }
    [CommandOption("--limit <COUNT>")] public int? Limit { get; init; }
}

internal sealed class ListProjectsCommand : AsyncCommand<ListProjectsSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, ListProjectsSettings settings, CancellationToken cancellationToken)
    {
        string? invalid = CommandOutput.Validate(settings.Output, settings.Json);
        if (settings.Limit is <= 0)
            invalid = "--limit must be greater than zero.";
        if (settings.Filter is not null && string.IsNullOrWhiteSpace(settings.Filter))
            invalid = "--filter must contain a nonblank project-name fragment.";
        if (settings.Preset is not null && string.IsNullOrWhiteSpace(settings.Preset))
            invalid = "--preset must contain a nonblank preset name.";
        if (invalid is not null)
            return Task.FromResult(CommandOutput.Failure(settings.Output, "projects.list", "invalid_options", invalid, 2));

        return Task.FromResult(CommandInspection.Run(settings.Output, "projects.list",
            () => Inspect(settings, cancellationToken), preserveLegacyExceptions: settings.Output is null));
    }

    private static int Inspect(ListProjectsSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var repository = new SqliteRepository(ConfigHelper.ResolveDb(settings.Db), readOnly: settings.Output is not null);
        if (settings.Output is null)
            repository.EnsureSchema();
        Project[] all = repository.ListProjects().ToArray();
        Project[] matched = all.Where(project =>
            (settings.Filter is null || project.Name.Contains(settings.Filter, StringComparison.OrdinalIgnoreCase)) &&
            (settings.Preset is null || string.Equals(project.Preset, settings.Preset, StringComparison.OrdinalIgnoreCase))).ToArray();
        Project[] rows = settings.Limit.HasValue ? matched.Take(settings.Limit.Value).ToArray() : matched;
        if (CommandOutput.IsJson(settings.Output))
            return CommandOutput.Success("projects.list", new
            {
                projects = rows.Select(ProjectInspection.Describe).ToArray(),
                count = rows.Length,
                matchedCount = matched.Length,
                totalCount = all.Length
            });
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(rows.Select(project => new
            {
                project.Name, project.RootPath, project.Preset, CreatedUtc = project.CreatedUtc.ToString("u")
            }), CommandJsonOptions.Indented));
            return 0;
        }
        ProjectInspection.WriteTable(rows, includeId: settings.Output is not null);
        return 0;
    }
}

internal sealed class ShowProjectSettings : CommandSettings
{
    [CommandArgument(0, "[name]")] public string? Name { get; init; }
    [CommandOption("--id <ID>")] public int? Id { get; init; }
    [CommandOption("--db <PATH>")] public string? Db { get; init; }
    [CommandOption("--output <FORMAT>")] public string? Output { get; init; }
}

internal sealed class ShowProjectCommand : AsyncCommand<ShowProjectSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, ShowProjectSettings settings, CancellationToken cancellationToken)
    {
        string? invalid = CommandOutput.Validate(settings.Output);
        if ((settings.Name is null) == (settings.Id is null))
            invalid = "Provide exactly one project selector: a literal name or --id.";
        if (settings.Name is not null && string.IsNullOrWhiteSpace(settings.Name))
            invalid = "Project name must not be blank.";
        if (settings.Id is <= 0)
            invalid = "--id must be greater than zero.";
        if (invalid is not null)
            return Task.FromResult(CommandOutput.Failure(settings.Output, "projects.show", "invalid_options", invalid, 2));

        return Task.FromResult(CommandInspection.Run(settings.Output, "projects.show", () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repository = new SqliteRepository(ConfigHelper.ResolveDb(settings.Db), readOnly: true);
            Project? project = settings.Id.HasValue
                ? repository.GetProjectById(settings.Id.Value)
                : repository.GetProjectByName(settings.Name!);
            if (project is null)
                return CommandOutput.Failure(settings.Output, "projects.show", "project_not_found",
                    "No registered project matches the supplied selector.", 2);
            if (CommandOutput.IsJson(settings.Output))
                return CommandOutput.Success("projects.show", new { project = ProjectInspection.Describe(project) });
            ProjectInspection.WriteTable([project], includeId: true);
            return 0;
        }));
    }
}

internal static class ProjectInspection
{
    internal sealed record ProjectSummary(int Id, string ExternalId, string Name, string RootPath, string Preset, string CreatedUtc, bool NeedsRestore);

    public static ProjectSummary Describe(Project project) => new(project.Id, project.ExternalId, project.Name,
        project.RootPath, project.Preset, DateTime.SpecifyKind(project.CreatedUtc, DateTimeKind.Utc).ToString("O"), project.NeedsRestore);

    public static void WriteTable(Project[] rows, bool includeId)
    {
        var table = new Table().Border(TableBorder.Rounded);
        if (includeId)
            table.AddColumn("ID");
        table.AddColumn("Name");
        table.AddColumn(new TableColumn("Path").NoWrap());
        table.AddColumn("Preset");
        table.AddColumn("Created (UTC)");
        foreach (Project project in rows)
        {
            string[] cells = [Markup.Escape(project.Name), Markup.Escape(project.RootPath),
                Markup.Escape(project.Preset), project.CreatedUtc.ToString("u")];
            table.AddRow(includeId ? [project.Id.ToString(), .. cells] : cells);
        }
        AnsiConsole.Write(table);
    }
}

internal static class CommandInspection
{
    public static int Run(string? output, string operation, Func<int> action, bool preserveLegacyExceptions = false)
    {
        try
        {
            return action();
        }
        catch (Exception error) when (!preserveLegacyExceptions && error is SqliteException or IOException or UnauthorizedAccessException)
        {
            return CommandOutput.Failure(output, operation, "repository_unavailable",
                "Cannot inspect the database. Check --db, access permissions, and supported schema; initialize or migrate it explicitly.", 1);
        }
    }

 }
