using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.CLI.Config;

namespace VaultSync.CLI.Commands
{
    sealed class AddProjectSettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandArgument(1, "<path>")] public string PathArg { get; init; } = "";
        [CommandOption("--preset")] public string Preset { get; init; } = "custom";
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
    }

    sealed class AddProjectCommand : AsyncCommand<AddProjectSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, AddProjectSettings s, CancellationToken cancellationToken)
        {
            string db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            string fullPath = Path.GetFullPath(s.PathArg.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException(fullPath);

            if (repo.GetProjectByName(s.Name) is not null)
                throw new Exception($"Project '{s.Name}' already exists");

            int id = repo.AddProject(new Project { Name = s.Name, RootPath = fullPath, Preset = s.Preset });

            if (!s.Quiet)
                AnsiConsole.MarkupLine($"[green]Added[/] project [bold]{Markup.Escape(s.Name)}[/] (id {id}) -> {Markup.Escape(fullPath)} [grey](preset: {Markup.Escape(s.Preset)})[/]");

            return Task.FromResult(0);
        }
    }

    sealed class RemoveProjectSettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--yes")] public bool Yes { get; init; } = false;
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
    }

    sealed class RemoveProjectCommand : AsyncCommand<RemoveProjectSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, RemoveProjectSettings s, CancellationToken cancellationToken)
        {
            string db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            Project proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");

            if (!s.Yes && !s.Quiet)
            {
                bool confirm = AnsiConsole.Confirm($"Delete project [bold]{Markup.Escape(s.Name)}[/] and all its snapshots/files?");
                if (!confirm) { AnsiConsole.MarkupLine("[yellow]Aborted[/]"); return Task.FromResult(1); }
            }

            DeleteStats stats = repo.DeleteProjectCascade(s.Name);
            if (stats.Projects == 0) throw new Exception($"Project '{s.Name}' not found (nothing deleted)");

            if (!s.Quiet)
                AnsiConsole.MarkupLine($"[green]Removed[/] project [bold]{Markup.Escape(s.Name)}[/] - Snapshots: {stats.Snapshots}, Files: {stats.Files}");

            return Task.FromResult(0);
        }
    }

    sealed class SetPathSettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandArgument(1, "<newPath>")] public string NewPath { get; init; } = "";
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
    }

    sealed class SetPathCommand : AsyncCommand<SetPathSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, SetPathSettings s, CancellationToken cancellationToken)
        {
            string db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            string full = Path.GetFullPath(s.NewPath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);

            if (!repo.UpdateProjectPath(s.Name, full, out string? oldPath))
                throw new Exception($"Project '{s.Name}' not found");

            if (!s.Quiet)
                AnsiConsole.MarkupLine($"[green]Updated[/] [bold]{Markup.Escape(s.Name)}[/] path: {Markup.Escape(oldPath ?? "?")} -> {Markup.Escape(full)}");

            return Task.FromResult(0);
        }
    }

    sealed class ListProjectsSettings : CommandSettings
    {
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--json")] public bool Json { get; init; } = false;
    }

    sealed class ListProjectsCommand : AsyncCommand<ListProjectsSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, ListProjectsSettings s, CancellationToken cancellationToken)
        {
            string db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            IEnumerable<Project> rows = repo.ListProjects();
            if (s.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(rows.Select(r => new
                {
                    r.Name, r.RootPath, r.Preset, CreatedUtc = r.CreatedUtc.ToString("u")
                }), CommandJsonOptions.Indented));
                return Task.FromResult(0);
            }

            Table table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Name");
            table.AddColumn(new TableColumn("Path").NoWrap());
            table.AddColumn("Preset");
            table.AddColumn("Created (UTC)");

            foreach (Project p in rows) table.AddRow(p.Name, p.RootPath, p.Preset, p.CreatedUtc.ToString("u"));
            AnsiConsole.Write(table);
            return Task.FromResult(0);
        }
    }

    sealed class DiscoverProjectsSettings : CommandSettings
    {
        [CommandOption("--root")] public string? OverrideRoot { get; init; }
        [CommandOption("--json")] public bool Json { get; init; } = false;
    }

    sealed class DiscoverProjectsCommand : AsyncCommand<DiscoverProjectsSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, DiscoverProjectsSettings s, CancellationToken cancellationToken)
        {
            AppConfig config = ConfigHelper.Load();

            if (!string.IsNullOrWhiteSpace(s.OverrideRoot))
            {
                config.ProjectsRoot = s.OverrideRoot;
            }

            var discovery = new ProjectDiscoveryService();
            IReadOnlyList<DiscoveredProject> projects = await discovery.DiscoverAsync(config, cancellationToken);

            if (s.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(projects, CommandJsonOptions.Indented));
                return 0;
            }

            if (projects.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No projects found.[/] Check your Projects Root in settings or use --root to override.");
                return 0;
            }

            Table table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Name");
            table.AddColumn(new TableColumn("Path").NoWrap());
            table.AddColumn("Last snapshot");
            table.AddColumn("Last size");

            foreach (DiscoveredProject p in projects)
            {
                string lastSnapshot = p.LastSnapshotTime?.ToString("u") ?? "-";
                string size = p.LastSnapshotSizeBytes.HasValue
                    ? ByteSizeFormat.FormatBytes(p.LastSnapshotSizeBytes.Value, "0.#")
                    : "-";
                table.AddRow(p.Name, p.Path, lastSnapshot, size);
            }

            AnsiConsole.Write(table);
            return 0;
        }
    }
}
