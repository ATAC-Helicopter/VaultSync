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
        public override Task<int> ExecuteAsync(CommandContext context, AddProjectSettings s, CancellationToken ct)
        {
            var db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            var fullPath = Path.GetFullPath(s.PathArg.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException(fullPath);

            if (repo.GetProjectByName(s.Name) is not null)
                throw new Exception($"Project '{s.Name}' already exists");

            var id = repo.AddProject(new Project { Name = s.Name, RootPath = fullPath, Preset = s.Preset });

            if (!s.Quiet)
                AnsiConsole.MarkupLine($"[green]Added[/] project [bold]{Markup.Escape(s.Name)}[/] (id {id}) → {Markup.Escape(fullPath)} [grey](preset: {Markup.Escape(s.Preset)})[/]");

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
        public override Task<int> ExecuteAsync(CommandContext context, RemoveProjectSettings s, CancellationToken ct)
        {
            var db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            var proj = repo.GetProjectByName(s.Name);
            if (proj is null) throw new Exception($"Project '{s.Name}' not found");

            if (!s.Yes && !s.Quiet)
            {
                var confirm = AnsiConsole.Confirm($"Delete project [bold]{Markup.Escape(s.Name)}[/] and all its snapshots/files?");
                if (!confirm) { AnsiConsole.MarkupLine("[yellow]Aborted[/]"); return Task.FromResult(1); }
            }

            var stats = repo.DeleteProjectCascade(s.Name);
            if (stats.Projects == 0) throw new Exception($"Project '{s.Name}' not found (nothing deleted)");

            if (!s.Quiet)
                AnsiConsole.MarkupLine($"[green]Removed[/] project [bold]{Markup.Escape(s.Name)}[/] — Snapshots: {stats.Snapshots}, Files: {stats.Files}");

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
        public override Task<int> ExecuteAsync(CommandContext context, SetPathSettings s, CancellationToken ct)
        {
            var db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            var full = Path.GetFullPath(s.NewPath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);

            if (!repo.UpdateProjectPath(s.Name, full, out var oldPath))
                throw new Exception($"Project '{s.Name}' not found");

            if (!s.Quiet)
                AnsiConsole.MarkupLine($"[green]Updated[/] [bold]{Markup.Escape(s.Name)}[/] path: {Markup.Escape(oldPath ?? "?")} → {Markup.Escape(full)}");

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
        public override Task<int> ExecuteAsync(CommandContext context, ListProjectsSettings s, CancellationToken ct)
        {
            var db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            var rows = repo.ListProjects();
            if (s.Json)
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                Console.WriteLine(JsonSerializer.Serialize(rows.Select(r => new
                {
                    r.Name, r.RootPath, r.Preset, CreatedUtc = r.CreatedUtc.ToString("u")
                }), options));
                return Task.FromResult(0);
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Name");
            table.AddColumn(new TableColumn("Path").NoWrap());
            table.AddColumn("Preset");
            table.AddColumn("Created (UTC)");

            foreach (var p in rows) table.AddRow(p.Name, p.RootPath, p.Preset, p.CreatedUtc.ToString("u"));
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
        public override async Task<int> ExecuteAsync(CommandContext context, DiscoverProjectsSettings s, CancellationToken ct)
        {
            var config = AppConfigStore.Load();

            if (!string.IsNullOrWhiteSpace(s.OverrideRoot))
            {
                config.ProjectsRoot = s.OverrideRoot;
            }

            var discovery = new ProjectDiscoveryService();
            var projects = await discovery.DiscoverAsync(config, ct);

            if (s.Json)
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                Console.WriteLine(JsonSerializer.Serialize(projects, options));
                return 0;
            }

            if (projects.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No projects found.[/] Check your Projects Root in settings or use --root to override.");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Name");
            table.AddColumn(new TableColumn("Path").NoWrap());
            table.AddColumn("Last snapshot");
            table.AddColumn("Last size");

            foreach (var p in projects)
            {
                var lastSnapshot = p.LastSnapshotTime?.ToString("u") ?? "-";
                var size = p.LastSnapshotSizeBytes.HasValue ? FormatSize(p.LastSnapshotSizeBytes.Value) : "-";
                table.AddRow(p.Name, p.Path, lastSnapshot, size);
            }

            AnsiConsole.Write(table);
            return 0;
        }

        private static string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return $"{len:0.#} {sizes[order]}";
        }
    }
}