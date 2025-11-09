using System;
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

namespace VaultSync.CLI.Commands
{
    sealed class SyncSettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandArgument(1, "<destination>")] public string Destination { get; init; } = "";
        [CommandOption("--dry-run")] public bool DryRun { get; init; } = false;
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
    }

    sealed class SyncCommand : AsyncCommand<SyncSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, SyncSettings s, CancellationToken ct)
        {
            var db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            var proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");
            var dest = s.Destination.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            var svc = new SyncService();

            if (!s.Quiet)
            {
                if (s.DryRun)
                    AnsiConsole.MarkupLine($"[yellow]Dry run[/]: mirroring [blue]{Markup.Escape(proj.RootPath)}[/] → [blue]{Markup.Escape(dest)}[/] (preset: {Markup.Escape(proj.Preset)})");
                else
                    AnsiConsole.MarkupLine($"Mirroring [blue]{Markup.Escape(proj.RootPath)}[/] → [blue]{Markup.Escape(dest)}[/] (preset: {Markup.Escape(proj.Preset)})");
            }

            var started = DateTime.UtcNow;
            var code = await svc.SyncAsync(proj, dest, s.DryRun, ct);
            var took = DateTime.UtcNow - started;

            if (!s.Quiet)
            {
                if (code == 0) AnsiConsole.MarkupLine($"[green]Sync complete[/] in {took.TotalSeconds:F1}s (exit 0)");
                else AnsiConsole.MarkupLine($"[red]Sync failed[/] (exit {code})");
            }

            return code;
        }
    }

    sealed class VerifySettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandArgument(1, "<from>")] public string From { get; init; } = "";
        [CommandOption("--percent")] public int Percent { get; init; } = 10;
        [CommandOption("--full")] public bool Full { get; init; } = false;
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
        [CommandOption("--json")] public bool Json { get; init; } = false;
    }

    sealed class VerifyCommand : AsyncCommand<VerifySettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, VerifySettings s, CancellationToken ct)
        {
            var db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            var proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");
            var src = s.From.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            var svc = new VerifyService(repo, new HashService());

            if (!s.Quiet && !s.Json)
                AnsiConsole.MarkupLine($"Verifying [blue]{Markup.Escape(proj.Name)}[/] against [blue]{Markup.Escape(src)}[/] — {(s.Full ? "full scan" : $"{s.Percent}% sample")}…");

            var started = DateTime.UtcNow;
            var result = await svc.VerifyAsync(proj, src, s.Percent, s.Full, ct);
            var took = DateTime.UtcNow - started;

            if (s.Json)
            {
                var payload = new
                {
                    project = proj.Name,
                    from = src,
                    full = s.Full,
                    percent = s.Percent,
                    checkedFiles = result.Checked,
                    failures = result.Failures.Select(f => new { f.Reason, f.RelPath, expected = f.expected, actual = f.actual }).ToList(),
                    tookSeconds = Math.Round(took.TotalSeconds, 3),
                    exitCode = result.Failures.Count == 0 ? 0 : 2
                };
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                return result.Failures.Count == 0 ? 0 : 2;
            }

            if (result.Failures.Count == 0)
            {
                if (!s.Quiet)
                    AnsiConsole.MarkupLine($"[green]OK[/] — Checked {result.Checked} files, all good ({took.TotalSeconds:F1}s).");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Reason");
            table.AddColumn("Path");
            table.AddColumn("Expected");
            table.AddColumn("Actual");
            foreach (var f in result.Failures.Take(100))
                table.AddRow(f.Reason, f.RelPath, f.expected ?? "-", f.actual ?? "-");
            AnsiConsole.Write(table);

            if (!s.Quiet)
                AnsiConsole.MarkupLine($"[red]FAILED[/] — {result.Failures.Count} issues out of {result.Checked} checked ({took.TotalSeconds:F1}s).");

            return 2;
        }
    }

    sealed class RestoreSettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandArgument(1, "<destination>")] public string Destination { get; init; } = "";
        [CommandOption("--snapshot")] public int? Snapshot { get; init; }
        [CommandOption("--dry-run")] public bool DryRun { get; init; } = false;
        [CommandOption("--clean")] public bool Clean { get; init; } = false;
        [CommandOption("--keep-empty-dirs")] public bool KeepEmptyDirs { get; init; } = false;
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
        [CommandOption("--json")] public bool Json { get; init; } = false;
    }

    sealed class RestoreCommand : AsyncCommand<RestoreSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, RestoreSettings s, CancellationToken ct)
        {
            var db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            var proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");

            int snapshotId;
            DateTime snapshotCreatedUtc;

            if (s.Snapshot is int explicitId)
            {
                snapshotId = explicitId;
                var found = repo.GetSnapshotsForProject(proj.Name).FirstOrDefault(x => x.Id == snapshotId)
                    ?? throw new Exception($"Snapshot {snapshotId} not found for project '{proj.Name}'");
                snapshotCreatedUtc = found.CreatedUtc;
            }
            else
            {
                var latest = repo.GetSnapshotsForProject(proj.Name).FirstOrDefault()
                    ?? throw new Exception("No snapshots exist for this project");
                snapshotId = latest.Id;
                snapshotCreatedUtc = latest.CreatedUtc;
            }

            var snapFiles = repo.GetFilesForSnapshot(snapshotId).ToList();
            if (!snapFiles.Any()) throw new Exception($"Snapshot {snapshotId} has no files");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var destRoot = s.Destination.Replace("~", home);
            var started = DateTime.UtcNow;

            var projRootFull = Path.GetFullPath(proj.RootPath);
            var destRootFull = Path.GetFullPath(destRoot);
            if (s.Clean && destRootFull.StartsWith(projRootFull, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Refusing to --clean a destination that is inside the project root.");

            Directory.CreateDirectory(destRootFull);

            var targetRelSet = new HashSet<string>(snapFiles.Select(f => f.RelPath), StringComparer.OrdinalIgnoreCase);

            int deleted = 0;
            if (s.Clean)
            {
                var existing = Directory.Exists(destRootFull)
                    ? Directory.EnumerateFiles(destRootFull, "*", SearchOption.AllDirectories).ToList()
                    : new List<string>();

                foreach (var full in existing)
                {
                    var rel = Path.GetRelativePath(destRootFull, full).Replace('\\', '/');
                    if (!targetRelSet.Contains(rel))
                    {
                        if (!s.Quiet && !s.Json) AnsiConsole.MarkupLine($"[red]- delete[/] {Markup.Escape(rel)}");
                        if (!s.DryRun) File.Delete(full);
                        deleted++;
                    }
                }
            }

            int copied = 0, skippedMissing = 0;
            foreach (var f in snapFiles)
            {
                var srcFull = Path.Combine(proj.RootPath, f.RelPath);
                var dstFull = Path.Combine(destRootFull, f.RelPath);

                if (!File.Exists(srcFull))
                {
                    skippedMissing++;
                    if (!s.Quiet && !s.Json) AnsiConsole.MarkupLine($"[yellow]! missing source[/] {Markup.Escape(f.RelPath)}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dstFull)!);
                if (!s.Quiet && !s.Json) AnsiConsole.MarkupLine($"[green]+ write[/] {Markup.Escape(f.RelPath)}");
                if (!s.DryRun) File.Copy(srcFull, dstFull, overwrite: true);
                copied++;
            }

            int deletedDirs = 0;
            if (s.Clean && !s.KeepEmptyDirs)
            {
                var allDirs = Directory.EnumerateDirectories(destRootFull, "*", SearchOption.AllDirectories)
                                       .OrderByDescending(d => d.Length);
                foreach (var dir in allDirs)
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        if (!s.DryRun) Directory.Delete(dir);
                        deletedDirs++;
                        if (!s.Quiet && !s.Json)
                            AnsiConsole.MarkupLine($"[red]- rmdir[/] {Markup.Escape(Path.GetRelativePath(destRootFull, dir).Replace('\\','/'))}");
                    }
                }
            }

            var took = DateTime.UtcNow - started;

            if (s.Json)
            {
                var payload = new
                {
                    project = proj.Name,
                    snapshotId,
                    snapshotCreatedUtc = snapshotCreatedUtc.ToString("u"),
                    destination = destRootFull,
                    dryRun = s.DryRun,
                    clean = s.Clean,
                    keepEmptyDirs = s.KeepEmptyDirs,
                    deleted,
                    deletedDirs,
                    copied,
                    missingFromSource = skippedMissing,
                    tookSeconds = Math.Round(took.TotalSeconds, 3),
                    exitCode = skippedMissing > 0 ? 1 : 0
                };
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            else if (!s.Quiet)
            {
                var hdr = s.DryRun ? "[yellow]Dry restore[/]" : "Restore";
                AnsiConsole.MarkupLine($"{hdr}: [bold]{Markup.Escape(proj.Name)}[/] snapshot [bold]{snapshotId}[/] ([grey]{snapshotCreatedUtc:u}[/]) → [blue]{Markup.Escape(destRootFull)}[/]");
                if (s.Clean && s.DryRun) AnsiConsole.MarkupLine("[grey]Note[/]: --clean will remove extra files (shown only).");
                if (s.Clean && !s.DryRun)
                {
                    var note = s.KeepEmptyDirs ? "[grey]Cleaning destination (files only; preserving empty dirs)…[/]"
                                               : "[grey]Cleaning destination (files + empty dirs)…[/]";
                    AnsiConsole.MarkupLine(note);
                }

                var mode = s.DryRun ? "[yellow]Dry restore complete[/]" : "[green]Restore complete[/]";
                AnsiConsole.MarkupLine($"{mode} — copied: {copied}, deleted: {deleted}, deleted-dirs: {deletedDirs}, missing-from-source: {skippedMissing} ({took.TotalSeconds:F1}s).");
                if (skippedMissing > 0)
                    AnsiConsole.MarkupLine("[yellow]Note[/]: Some files listed in the snapshot were not present in the current project folder; they were skipped.");
            }

            return skippedMissing > 0 ? 1 : 0;
        }
    }

    sealed class SelfTestSettings : CommandSettings
    {
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
    }

    sealed class SelfTestCommand : AsyncCommand<SelfTestSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, SelfTestSettings s, CancellationToken ct)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var tmpRoot = Path.Combine(home, ".vaultsync", "selftest");
            var src = Path.Combine(tmpRoot, "src");
            var dst = Path.Combine(tmpRoot, "dst");

            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);

            await File.WriteAllTextAsync(Path.Combine(src, "a.txt"), "hello", ct);
            Directory.CreateDirectory(Path.Combine(src, "Sub"));
            await File.WriteAllTextAsync(Path.Combine(src, "Sub", "b.txt"), "world", ct);

            var name = $"SelfTest-{DateTime.UtcNow:yyyyMMddHHmmss}";
            var db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            if (!s.Quiet) AnsiConsole.MarkupLine($"[blue]Self-test[/] using database {Markup.Escape(db)}");

            var id = repo.AddProject(new VaultSync.Core.Models.Project { Name = name, RootPath = src, Preset = "custom" });
            if (!s.Quiet) AnsiConsole.MarkupLine($"Registered project [bold]{Markup.Escape(name)}[/] (id {id}) → {Markup.Escape(src)}");

            var snapSvc = new SnapshotService(repo, new HashService());
            var snapId = await snapSvc.CreateSnapshotAsync(repo.GetProjectByName(name)!, fullHash: true, ct);
            if (!s.Quiet) AnsiConsole.MarkupLine($"Snapshot {snapId} created");

            var syncSvc = new SyncService();
            var syncCode = await syncSvc.SyncAsync(repo.GetProjectByName(name)!, dst, dryRun: false, ct);
            if (syncCode != 0) { if (!s.Quiet) AnsiConsole.MarkupLine($"[red]Sync failed[/] (exit {syncCode})"); return 2; }
            if (!s.Quiet) AnsiConsole.MarkupLine($"[green]Sync OK[/] → {Markup.Escape(dst)}");

            var verifySvc = new VerifyService(repo, new HashService());
            var result = await verifySvc.VerifyAsync(repo.GetProjectByName(name)!, dst, percent: 100, full: true, ct);
            if (result.Failures.Any())
            {
                if (!s.Quiet) AnsiConsole.MarkupLine($"[red]Verify failed[/]: {result.Failures.Count} issues");
                return 2;
            }
            if (!s.Quiet) AnsiConsole.MarkupLine("[green]Verify OK[/] — all files matched");

            repo.DeleteProjectCascade(name);
            if (!s.Quiet) AnsiConsole.MarkupLine($"[grey]Cleanup[/]: removed project metadata; files remain under {Markup.Escape(tmpRoot)}");

            return 0;
        }
    }
}