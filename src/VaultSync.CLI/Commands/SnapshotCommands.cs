using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.CLI.Config;
using VaultSync.CLI.Utils;

namespace VaultSync.CLI.Commands
{
    sealed class SnapshotSettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandOption("--full-hash")] public bool FullHash { get; init; } = true;
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
    }

    sealed class SnapshotCommand : AsyncCommand<SnapshotSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, SnapshotSettings s, CancellationToken cancellationToken)
        {
            string db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            Core.Models.Project proj = repo.GetProjectByName(s.Name) ?? throw new InvalidOperationException($"Project '{s.Name}' not found.");

            var svc = new SnapshotService(repo, new HashService());

            Log.Info($"snapshot start name={proj.Name} fullHash={s.FullHash} root={proj.RootPath}");

            if (!s.Quiet)
                AnsiConsole.MarkupLine($"[blue]Scanning & hashing[/] {Markup.Escape(proj.Name)} at {Markup.Escape(proj.RootPath)} (preset: {Markup.Escape(proj.Preset)})...");

            DateTime started = DateTime.UtcNow;
            int snapId = await svc.CreateSnapshotAsync(
                proj,
                s.FullHash,
                maxSnapshotsToKeep: null,
                ct: cancellationToken);
            TimeSpan took = DateTime.UtcNow - started;
            SnapshotOutcome? outcome = SnapshotService.LastOutcome;

            if (!s.Quiet)
            {
                AnsiConsole.MarkupLine($"[green]Snapshot {snapId} created[/] in {took.TotalSeconds:F1}s");
                if (outcome is not null)
                {
                    AnsiConsole.MarkupLine($"[grey]Added[/]: {outcome.Added}, [grey]Modified[/]: {outcome.Modified}, [grey]Deleted[/]: {outcome.Deleted}, [grey]Unchanged[/]: {outcome.Unchanged}, [grey]Total files[/]: {outcome.TotalFiles}, [grey]Bytes[/]: {ByteSizeFormat.FormatBytes(outcome.TotalBytes, "0.#")}");
                }
            }

            Log.Info($"snapshot done id={snapId} took={took.TotalMilliseconds:F0}ms added={outcome?.Added} modified={outcome?.Modified} deleted={outcome?.Deleted} unchanged={outcome?.Unchanged} total={outcome?.TotalFiles} bytes={outcome?.TotalBytes}");
            return 0;
        }
    }

    sealed class HistorySettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandOption("--db <PATH>")] public string? Db { get; init; }
        [CommandOption("--json")] public bool Json { get; init; }
        [CommandOption("--output <FORMAT>")] public string? Output { get; init; }
        [CommandOption("--limit <COUNT>")] public int? Limit { get; init; }
    }

    sealed class HistoryCommand : AsyncCommand<HistorySettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, HistorySettings settings, CancellationToken cancellationToken)
        {
            string? invalid = CommandOutput.Validate(settings.Output, settings.Json);
            if (settings.Output is not null && settings.Limit is <= 0)
                invalid = "--limit must be greater than zero.";
            if (invalid is not null)
                return Task.FromResult(CommandOutput.Failure(settings.Output, "snapshots.list", "invalid_options", invalid, 2));

            return Task.FromResult(CommandInspection.Run(settings.Output, "snapshots.list",
                () => Inspect(settings, cancellationToken), preserveLegacyExceptions: settings.Output is null));
        }

        private static int Inspect(HistorySettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repository = new SqliteRepository(ConfigHelper.ResolveDb(settings.Db), readOnly: settings.Output is not null);
            if (settings.Output is null)
                repository.EnsureSchema();
            Core.Models.Project? project = repository.GetProjectByName(settings.Name);
            if (project is null)
            {
                if (settings.Output is null)
                    throw new InvalidOperationException($"Project '{settings.Name}' not found.");
                return CommandOutput.Failure(settings.Output, "snapshots.list", "project_not_found",
                    "No registered project matches the supplied name.", 2);
            }

            Core.Models.Snapshot[] all = repository.GetSnapshotsForProject(project.Name).ToArray();
            Core.Models.Snapshot[] rows = settings.Limit is int limit && limit > 0 ? all.Take(limit).ToArray() : all;
            Log.Info($"history name={project.Name} count={rows.Length} json={settings.Json || CommandOutput.IsJson(settings.Output)}");

            if (CommandOutput.IsJson(settings.Output))
                return CommandOutput.Success("snapshots.list", new
                {
                    project = ProjectInspection.Describe(project),
                    snapshots = rows.Select(Describe).ToArray(),
                    count = rows.Length,
                    totalCount = all.Length
                });
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(rows.Select(snapshot => new
                {
                    snapshot.Id, CreatedUtc = snapshot.CreatedUtc.ToString("u"), snapshot.FileCount, snapshot.TotalBytes
                }), CommandJsonOptions.Indented));
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Snapshot");
            table.AddColumn("Created (UTC)");
            table.AddColumn(new TableColumn("Files").RightAligned());
            table.AddColumn(new TableColumn("Bytes").RightAligned());
            foreach (Core.Models.Snapshot snapshot in rows)
                table.AddRow(snapshot.Id.ToString(), snapshot.CreatedUtc.ToString("u"), snapshot.FileCount.ToString(), ByteSizeFormat.FormatBytes(snapshot.TotalBytes, "0.#"));
            AnsiConsole.MarkupLine($"History for [bold]{Markup.Escape(project.Name)}[/] - {rows.Length} snapshot(s)");
            AnsiConsole.Write(table);
            return 0;
        }

        private static object Describe(Core.Models.Snapshot snapshot) => new
        {
            snapshot.Id,
            createdUtc = DateTime.SpecifyKind(snapshot.CreatedUtc, DateTimeKind.Utc).ToString("O"),
            snapshot.FileCount,
            snapshot.TotalBytes
        };
    }

    sealed class DiffSettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandArgument(1, "[A]")] public int? A { get; init; }
        [CommandArgument(2, "[B]")] public int? B { get; init; }
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--limit")] public int Limit { get; init; } = 200;
        [CommandOption("--json")] public bool Json { get; init; } = false;
    }

    sealed class DiffCommand : AsyncCommand<DiffSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, DiffSettings s, CancellationToken cancellationToken)
        {
            string db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            Core.Models.Project proj = repo.GetProjectByName(s.Name) ?? throw new InvalidOperationException($"Project '{s.Name}' not found.");

            var snaps = repo.GetSnapshotsForProject(proj.Name).ToList();
            if (snaps.Count < 1) throw new InvalidOperationException("No snapshots exist for this project.");

            DiffSelection selection = ResolveDiffSelection(snaps, s);

            var aFiles = repo.GetFilesForSnapshot(selection.A).ToDictionary(f => f.RelPath, f => f);
            var bFiles = repo.GetFilesForSnapshot(selection.B).ToDictionary(f => f.RelPath, f => f);
            DiffResult diff = BuildDiff(aFiles, bFiles);

            Log.Info($"diff name={proj.Name} A={selection.A} B={selection.B} added={diff.Added.Count} deleted={diff.Deleted.Count} modified={diff.Modified.Count} unchanged={diff.Unchanged.Count} json={s.Json}");

            if (s.Json)
            {
                WriteDiffJson(selection, diff, aFiles.Count, bFiles.Count);
                return Task.FromResult(0);
            }

            WriteDiffTable(proj.Name, selection, diff, s.Limit);
            return Task.FromResult(0);
        }

        private static DiffSelection ResolveDiffSelection(IReadOnlyList<Core.Models.Snapshot> snaps, DiffSettings settings)
        {
            if (settings.A.HasValue && settings.B.HasValue)
                return new DiffSelection(settings.A.Value, settings.B.Value);

            if (settings.A.HasValue)
            {
                int idx = snaps.ToList().FindIndex(x => x.Id == settings.A.Value);
                if (idx < 0 || idx + 1 >= snaps.Count)
                    throw new InvalidOperationException("Cannot infer the other snapshot; provide both A and B.");

                return new DiffSelection(settings.A.Value, snaps[idx + 1].Id);
            }

            if (snaps.Count < 2)
                throw new InvalidOperationException("Need at least two snapshots to diff.");

            return new DiffSelection(snaps[0].Id, snaps[1].Id);
        }

        private static DiffResult BuildDiff(
            IReadOnlyDictionary<string, Core.Models.FileEntry> aFiles,
            IReadOnlyDictionary<string, Core.Models.FileEntry> bFiles)
        {
            var added = new List<string>();
            var deleted = new List<string>();
            var modified = new List<string>();
            var unchanged = new List<string>();

            foreach ((string rel, Core.Models.FileEntry af) in aFiles)
            {
                if (!bFiles.TryGetValue(rel, out Core.Models.FileEntry? bf))
                    added.Add(rel);
                else if (FileChanged(af, bf))
                    modified.Add(rel);
                else
                    unchanged.Add(rel);
            }

            deleted.AddRange(bFiles.Keys.Where(rel => !aFiles.ContainsKey(rel)));
            return new DiffResult(added, deleted, modified, unchanged);
        }

        private static bool FileChanged(Core.Models.FileEntry a, Core.Models.FileEntry b) =>
            !string.Equals(a.HashSha256, b.HashSha256, StringComparison.OrdinalIgnoreCase) ||
            a.Size != b.Size;

        private static void WriteDiffJson(DiffSelection selection, DiffResult diff, int totalA, int totalB)
        {
            string json = JsonSerializer.Serialize(new {
                A = selection.A, B = selection.B,
                added = diff.Added,
                deleted = diff.Deleted,
                modified = diff.Modified,
                unchanged = diff.Unchanged,
                summary = new {
                    added = diff.Added.Count,
                    deleted = diff.Deleted.Count,
                    modified = diff.Modified.Count,
                    unchanged = diff.Unchanged.Count,
                    totalA,
                    totalB
                }
            }, CommandJsonOptions.Indented);
            Console.WriteLine(json);
        }

        private static void WriteDiffTable(string projectName, DiffSelection selection, DiffResult diff, int limit)
        {
            AnsiConsole.MarkupLine($"Diff [bold]{Markup.Escape(projectName)}[/] - A: {selection.A} vs B: {selection.B}");
            Grid grid = new Grid().AddColumn().AddColumn().AddColumn().AddColumn();
            grid.AddRow(
                $"[green]Added[/]: {diff.Added.Count}",
                $"[red]Deleted[/]: {diff.Deleted.Count}",
                $"[yellow]Modified[/]: {diff.Modified.Count}",
                $"[grey]Unchanged[/]: {diff.Unchanged.Count}");
            AnsiConsole.Write(grid);

            PrintList("ADDED", "green", diff.Added, limit);
            PrintList("DELETED", "red", diff.Deleted, limit);
            PrintList("MODIFIED", "yellow", diff.Modified, limit);
        }

        private static void PrintList(string title, string color, IEnumerable<string> rows, int limit)
        {
            int total = rows is ICollection<string> c ? c.Count : rows.Count();
            var list = rows.Take(limit).ToList();
            if (list.Count == 0)
                return;

            Table table = new Table().Border(TableBorder.Rounded);
            table.Title = new TableTitle($"[{color}]{title}[/] (showing {list.Count}{(total > list.Count ? $"/{total}" : "")})");
            table.AddColumn("Path");
            foreach (string? r in list)
                table.AddRow(r);
            AnsiConsole.Write(table);
        }
    }

    sealed class PruneSettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandOption("--keep-last <N>")] public int? KeepLast { get; init; }
        [CommandOption("--before <YYYY-MM-DD>")] public string? Before { get; init; }
        [CommandOption("--dry-run")] public bool DryRun { get; init; } = false;
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
        [CommandOption("--json")] public bool Json { get; init; } = false;

        public override ValidationResult Validate()
        {
            if (KeepLast is null && string.IsNullOrWhiteSpace(Before))
                return ValidationResult.Error("Provide --keep-last or --before.");
            if (KeepLast is not null && !string.IsNullOrWhiteSpace(Before))
                return ValidationResult.Error("Use either --keep-last or --before, not both.");
            if (KeepLast is int n && n < 0)
                return ValidationResult.Error("--keep-last must be >= 0.");
            if (!string.IsNullOrWhiteSpace(Before) && !TryParseBeforeDate(Before, out _))
                return ValidationResult.Error("--before must be a date like 2025-11-08");
            return ValidationResult.Success();
        }

        internal static bool TryParseBeforeDate(string value, out DateTime result) =>
            DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out result);
    }

    sealed class PruneCommand : AsyncCommand<PruneSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, PruneSettings s, CancellationToken cancellationToken)
        {
            string db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            Core.Models.Project proj = repo.GetProjectByName(s.Name) ?? throw new InvalidOperationException($"Project '{s.Name}' not found.");

            var snaps = repo.GetSnapshotsForProject(proj.Name).ToList();
            if (snaps.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No snapshots to prune[/].");
                return Task.FromResult(0);
            }

            HashSet<int> protectedSnapshotIds = repo.GetBackupsForProject(proj.Id)
                .Select(static backup => backup.SnapshotId)
                .ToHashSet();
            IReadOnlyDictionary<int, Core.Models.SnapshotHistoryMetadata> metadata =
                repo.GetSnapshotHistoryMetadataBySnapshotIds(snaps.Select(static snapshot => snapshot.Id));
            protectedSnapshotIds.UnionWith(metadata
                .Where(static entry => entry.Value.IsProtected)
                .Select(static entry => entry.Key));
            List<int> planned = PlanPrune(snaps, s, protectedSnapshotIds);

            if (s.Json)
            {
                WritePruneJson(proj.Name, snaps.Count, planned, s.DryRun);
            }
            else
            {
                WritePruneTable(proj.Name, snaps, planned, s.DryRun);
            }

            if (planned.Count == 0 || s.DryRun) return Task.FromResult(0);

            (int snapshots, int files) = repo.DeleteSnapshotsById(proj.Name, planned);
            AnsiConsole.MarkupLine($"[green]Pruned[/] snapshots: {snapshots}, files: {files}");
            return Task.FromResult(0);
        }

        internal static List<int> PlanPrune(
            IReadOnlyList<Core.Models.Snapshot> snapshots,
            PruneSettings settings,
            IReadOnlySet<int> protectedSnapshotIds)
        {
            List<Core.Models.Snapshot> eligible = snapshots
                .Where(snapshot => !protectedSnapshotIds.Contains(snapshot.Id))
                .ToList();
            IEnumerable<int> toDelete = settings.KeepLast is int keep
                ? eligible.Skip(keep).Select(static snapshot => snapshot.Id)
                : eligible
                    .Where(snapshot => snapshot.CreatedUtc < ParseBeforeDate(settings.Before!))
                    .Select(static snapshot => snapshot.Id);
            return [.. toDelete.Distinct().Order()];
        }

        private static DateTime ParseBeforeDate(string value) =>
            PruneSettings.TryParseBeforeDate(value, out DateTime result)
                ? result.Date
                : throw new InvalidOperationException("The validated prune date is invalid.");

        private static void WritePruneJson(string projectName, int totalSnapshots, IReadOnlyList<int> planned, bool dryRun)
        {
            var payload = new { project = projectName, totalSnapshots, plannedDeletions = planned, dryRun };
            Console.WriteLine(JsonSerializer.Serialize(payload, CommandJsonOptions.Indented));
        }

        private static void WritePruneTable(
            string projectName,
            IReadOnlyList<Core.Models.Snapshot> snapshots,
            IReadOnlyList<int> planned,
            bool dryRun)
        {
            AnsiConsole.MarkupLine($"Prune [bold]{Markup.Escape(projectName)}[/]: total snapshots {snapshots.Count}");
            if (planned.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]Nothing to delete[/].");
                return;
            }

            WritePlannedPruneTable(snapshots, planned);
            if (dryRun)
                AnsiConsole.MarkupLine("[yellow]Dry run[/]: no changes written.");
        }

        private static void WritePlannedPruneTable(
            IReadOnlyList<Core.Models.Snapshot> snapshots,
            IEnumerable<int> planned)
        {
            Table tbl = new Table().Border(TableBorder.Rounded);
            tbl.AddColumn("Snapshot");
            tbl.AddColumn("Created (UTC)");
            tbl.AddColumn(new TableColumn("Files").RightAligned());
            tbl.AddColumn(new TableColumn("Bytes").RightAligned());

            var byId = snapshots.ToDictionary(x => x.Id);
            foreach (int id in planned)
            {
                Core.Models.Snapshot srow = byId[id];
                tbl.AddRow(id.ToString(), srow.CreatedUtc.ToString("u"), srow.FileCount.ToString(), ByteSizeFormat.FormatBytes(srow.TotalBytes, "0.#"));
            }
            AnsiConsole.Write(tbl);
        }
    }

    sealed record DiffSelection(int A, int B);
    sealed record DiffResult(
        List<string> Added,
        List<string> Deleted,
        List<string> Modified,
        List<string> Unchanged);
}
