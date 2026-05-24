using System;
using System.Collections.Generic;
using System.IO;
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
        protected override async Task<int> ExecuteAsync(CommandContext context, SnapshotSettings s, CancellationToken ct)
        {
            string db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            Core.Models.Project proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");

            var svc = new SnapshotService(repo, new HashService());

            Log.Info($"snapshot start name={proj.Name} fullHash={s.FullHash} root={proj.RootPath}");

            if (!s.Quiet)
                AnsiConsole.MarkupLine($"[blue]Scanning & hashing[/] {Markup.Escape(proj.Name)} at {Markup.Escape(proj.RootPath)} (preset: {Markup.Escape(proj.Preset)})...");

            DateTime started = DateTime.UtcNow;
            int snapId = await svc.CreateSnapshotAsync(
                proj,
                s.FullHash,
                maxSnapshotsToKeep: null,
                ct: ct);
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
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--json")] public bool Json { get; init; } = false;
        [CommandOption("--limit")] public int? Limit { get; init; }
    }

    sealed class HistoryCommand : AsyncCommand<HistorySettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, HistorySettings s, CancellationToken ct)
        {
            string db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            Core.Models.Project proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");

            IEnumerable<Core.Models.Snapshot> snaps = repo.GetSnapshotsForProject(proj.Name);
            if (s.Limit is int lim && lim > 0) snaps = snaps.Take(lim);

            var list = snaps.ToList();

            Log.Info($"history name={proj.Name} count={list.Count} json={s.Json}");

            if (s.Json)
            {
                string json = JsonSerializer.Serialize(
                    list.Select(x => new {
                        x.Id, CreatedUtc = x.CreatedUtc.ToString("u"), x.FileCount, x.TotalBytes
                    }),
                    CommandJsonOptions.Indented);
                Console.WriteLine(json);
                return Task.FromResult(0);
            }

            Table table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Snapshot");
            table.AddColumn("Created (UTC)");
            table.AddColumn(new TableColumn("Files").RightAligned());
            table.AddColumn(new TableColumn("Bytes").RightAligned());

            foreach (Core.Models.Snapshot? srow in list)
                table.AddRow(srow.Id.ToString(), srow.CreatedUtc.ToString("u"), srow.FileCount.ToString(), ByteSizeFormat.FormatBytes(srow.TotalBytes, "0.#"));

            AnsiConsole.MarkupLine($"History for [bold]{Markup.Escape(proj.Name)}[/] - {list.Count} snapshot(s)");
            AnsiConsole.Write(table);
            return Task.FromResult(0);
        }
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
        protected override Task<int> ExecuteAsync(CommandContext context, DiffSettings s, CancellationToken ct)
        {
            string db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            Core.Models.Project proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");

            var snaps = repo.GetSnapshotsForProject(proj.Name).ToList();
            if (snaps.Count < 1) throw new Exception("No snapshots exist for this project");

            int aId, bId;
            if (s.A.HasValue && s.B.HasValue)
            {
                aId = s.A.Value; bId = s.B.Value;
            }
            else if (s.A.HasValue)
            {
                aId = s.A.Value;
                int idx = snaps.FindIndex(x => x.Id == aId);
                if (idx < 0 || idx + 1 >= snaps.Count) throw new Exception("Cannot infer the other snapshot; provide both A and B.");
                bId = snaps[idx + 1].Id;
            }
            else
            {
                if (snaps.Count < 2) throw new Exception("Need at least two snapshots to diff");
                aId = snaps[0].Id; bId = snaps[1].Id;
            }

            var aFiles = repo.GetFilesForSnapshot(aId).ToDictionary(f => f.RelPath, f => f);
            var bFiles = repo.GetFilesForSnapshot(bId).ToDictionary(f => f.RelPath, f => f);

            var added = new List<string>();
            var deleted = new List<string>();
            var modified = new List<string>();
            var unchanged = new List<string>();

            foreach (KeyValuePair<string, Core.Models.FileEntry> kv in aFiles)
            {
                string rel = kv.Key;
                Core.Models.FileEntry af = kv.Value;
                if (!bFiles.TryGetValue(rel, out Core.Models.FileEntry? bf))
                {
                    added.Add(rel); continue;
                }
                if (!string.Equals(af.HashSha256, bf.HashSha256, StringComparison.OrdinalIgnoreCase) || af.Size != bf.Size)
                    modified.Add(rel);
                else
                    unchanged.Add(rel);
            }
            foreach (KeyValuePair<string, Core.Models.FileEntry> kv in bFiles)
            {
                string rel = kv.Key;
                if (!aFiles.ContainsKey(rel)) deleted.Add(rel);
            }

            Log.Info($"diff name={proj.Name} A={aId} B={bId} added={added.Count} deleted={deleted.Count} modified={modified.Count} unchanged={unchanged.Count} json={s.Json}");

            if (s.Json)
            {
                string json = JsonSerializer.Serialize(new {
                    A = aId, B = bId, added, deleted, modified, unchanged,
                    summary = new {
                        added = added.Count, deleted = deleted.Count,
                        modified = modified.Count, unchanged = unchanged.Count,
                        totalA = aFiles.Count, totalB = bFiles.Count
                    }
                }, CommandJsonOptions.Indented);
                Console.WriteLine(json);
                return Task.FromResult(0);
            }

            AnsiConsole.MarkupLine($"Diff [bold]{Markup.Escape(proj.Name)}[/] - A: {aId} vs B: {bId}");
            Grid grid = new Grid().AddColumn().AddColumn().AddColumn().AddColumn();
            grid.AddRow(
                $"[green]Added[/]: {added.Count}",
                $"[red]Deleted[/]: {deleted.Count}",
                $"[yellow]Modified[/]: {modified.Count}",
                $"[grey]Unchanged[/]: {unchanged.Count}");
            AnsiConsole.Write(grid);

            void PrintList(string title, string color, IEnumerable<string> rows)
            {
                int total = rows is ICollection<string> c ? c.Count : rows.Count();
                var list  = rows.Take(s.Limit).ToList();
                if (list.Count == 0) return;

                Table table = new Table().Border(TableBorder.Rounded);
                table.Title = new TableTitle($"[{color}]{title}[/] (showing {list.Count}{(total > list.Count ? $"/{total}" : "")})");
                table.AddColumn("Path");
                foreach (string? r in list) table.AddRow(r);
                AnsiConsole.Write(table);
            }

            PrintList("ADDED", "green", added);
            PrintList("DELETED", "red", deleted);
            PrintList("MODIFIED", "yellow", modified);

            return Task.FromResult(0);
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
            if (!string.IsNullOrWhiteSpace(Before) && !DateTime.TryParse(Before, out _))
                return ValidationResult.Error("--before must be a date like 2025-11-08");
            return ValidationResult.Success();
        }
    }

    sealed class PruneCommand : AsyncCommand<PruneSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, PruneSettings s, CancellationToken ct)
        {
            string db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            Core.Models.Project proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");

            var snaps = repo.GetSnapshotsForProject(proj.Name).ToList();
            if (snaps.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No snapshots to prune[/].");
                return Task.FromResult(0);
            }

            var toDelete = new List<int>();
            if (s.KeepLast is int keep)
            {
                if (snaps.Count > keep) toDelete.AddRange(snaps.Skip(keep).Select(x => x.Id));
            }
            else if (!string.IsNullOrWhiteSpace(s.Before))
            {
                var cutoff = DateTime.Parse(s.Before!).ToUniversalTime().Date;
                toDelete.AddRange(snaps.Where(x => x.CreatedUtc < cutoff).Select(x => x.Id));
            }

            List<int> planned = [.. toDelete.Distinct().Order()];

            if (s.Json)
            {
                var payload = new { project = proj.Name, totalSnapshots = snaps.Count, plannedDeletions = planned, dryRun = s.DryRun };
                Console.WriteLine(JsonSerializer.Serialize(payload, CommandJsonOptions.Indented));
            }
            else
            {
                AnsiConsole.MarkupLine($"Prune [bold]{Markup.Escape(proj.Name)}[/]: total snapshots {snaps.Count}");
                if (planned.Count == 0)
                {
                    AnsiConsole.MarkupLine("[grey]Nothing to delete[/].");
                }
                else
                {
                    Table tbl = new Table().Border(TableBorder.Rounded);
                    tbl.AddColumn("Snapshot");
                    tbl.AddColumn("Created (UTC)");
                    tbl.AddColumn(new TableColumn("Files").RightAligned());
                    tbl.AddColumn(new TableColumn("Bytes").RightAligned());

                    var byId = snaps.ToDictionary(x => x.Id);
                    foreach (int id in planned)
                    {
                        Core.Models.Snapshot srow = byId[id];
                        tbl.AddRow(id.ToString(), srow.CreatedUtc.ToString("u"), srow.FileCount.ToString(), ByteSizeFormat.FormatBytes(srow.TotalBytes, "0.#"));
                    }
                    AnsiConsole.Write(tbl);
                    if (s.DryRun) AnsiConsole.MarkupLine("[yellow]Dry run[/]: no changes written.");
                }
            }

            if (planned.Count == 0 || s.DryRun) return Task.FromResult(0);

            (int snapshots, int files) = repo.DeleteSnapshotsById(proj.Name, planned);
            AnsiConsole.MarkupLine($"[green]Pruned[/] snapshots: {snapshots}, files: {files}");
            return Task.FromResult(0);
        }
    }
}
