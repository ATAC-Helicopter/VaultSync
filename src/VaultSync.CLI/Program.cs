using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using System.Runtime.InteropServices;
using System.ComponentModel;

// ========= Config subsystem =========
sealed class AppConfig
{
    public string Database { get; set; } = "~/.vaultsync/vault.db";
}

static class ConfigHelper
{
    public static string GetConfigDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".vaultsync");
    }

    public static string GetConfigPath() => Path.Combine(GetConfigDir(), "config.json");

    public static void Save(AppConfig cfg)
    {
        var dir = GetConfigDir();
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));

        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(GetConfigPath(), json + Environment.NewLine);
    }

    public static AppConfig Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path))
            throw new Exception("Run `vaultsync init` first (creates ~/.vaultsync/config.json)");

        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        return cfg;
    }

    public static string ResolveDb(string? overridePath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath.Replace("~", home);

        var cfg = Load();
        return cfg.Database.Replace("~", home);
    }
}



// ===== CLI: init =====
sealed class InitSettings : CommandSettings
{
    [CommandOption("--db")] public string Db { get; init; } = "~/.vaultsync/vault.db";
    [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
}
sealed class InitCommand : AsyncCommand<InitSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, InitSettings s, CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expanded = s.Db.Replace("~", home);

        ConfigHelper.Save(new AppConfig { Database = expanded });

        if (!s.Quiet)
        {
            var dir = ConfigHelper.GetConfigDir();
            AnsiConsole.MarkupLine($"[green]Initialized config at[/] {Markup.Escape(dir)}");
            var pretty = JsonSerializer.Serialize(new AppConfig { Database = s.Db }, new JsonSerializerOptions { WriteIndented = true });
            AnsiConsole.WriteLine(pretty);
        }

        return Task.FromResult(0);
    }
}

// ========= Simple file logger (thread-safe) =========
static class Log
{
    static readonly object _gate = new();
    static string? _logFile;

    public static void Init()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(home, ".vaultsync", "logs");
            Directory.CreateDirectory(dir);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd");
            _logFile = Path.Combine(dir, $"vaultsync-{stamp}.log");

            // header once per process
            Write("INFO", "=== vaultsync start ===");
        }
        catch
        {
            // swallow logging init errors; CLI must not crash because of logging
        }
    }

    public static void Write(string level, string message)
    {
        try
        {
            if (_logFile is null) return;
            var line = $"{DateTime.UtcNow:O} [{level}] {message}{Environment.NewLine}";
            lock (_gate)
            {
                File.AppendAllText(_logFile, line);
            }
        }
        catch { /* never throw */ }
    }

    public static void Info(string message)  => Write("INFO",  message);
    public static void Warn(string message)  => Write("WARN",  message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Exception(Exception ex, string where)
    {
        Write("ERROR", $"{where}: {ex.GetType().Name}: {ex.Message}");
        Write("ERROR", ex.StackTrace ?? "(no stack)");
    }
}

// ===== CLI: self-test =====
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

        Log.Info($"self-test start db={s.Db ?? "(default)"} quiet={s.Quiet}");

        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        await File.WriteAllTextAsync(Path.Combine(src, "a.txt"), "hello", ct);
        Directory.CreateDirectory(Path.Combine(src, "Sub"));
        await File.WriteAllTextAsync(Path.Combine(src, "Sub", "b.txt"), "world", ct);

        var name = $"SelfTest-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var db = ConfigHelper.ResolveDb(s.Db);
        var repo = new SqliteRepository(db);
        repo.EnsureSchema();

        if (!s.Quiet)
            AnsiConsole.MarkupLine($"[blue]Self-test[/] using database {Markup.Escape(db)}");

        Log.Info($"self-test register name={name} src={src}");

        var id = repo.AddProject(new Project { Name = name, RootPath = src, Preset = "custom" });
        if (!s.Quiet)
            AnsiConsole.MarkupLine($"Registered project [bold]{Markup.Escape(name)}[/] (id {id}) → {Markup.Escape(src)}");

        var snapSvc = new SnapshotService(repo, new HashService());
        var snapId = await snapSvc.CreateSnapshotAsync(repo.GetProjectByName(name)!, fullHash: true, ct);
        if (!s.Quiet)
            AnsiConsole.MarkupLine($"Snapshot {snapId} created");
        Log.Info($"self-test snapshot id={snapId}");

        var syncSvc = new SyncService();
        var syncCode = await syncSvc.SyncAsync(repo.GetProjectByName(name)!, dst, dryRun: false, ct);
        Log.Info($"self-test sync exit={syncCode} dst={dst}");
        if (syncCode != 0)
        {
            if (!s.Quiet) AnsiConsole.MarkupLine($"[red]Sync failed[/] (exit {syncCode})");
            return 2;
        }
        if (!s.Quiet) AnsiConsole.MarkupLine($"[green]Sync OK[/] → {Markup.Escape(dst)}");

        var verifySvc = new VerifyService(repo, new HashService());
        var result = await verifySvc.VerifyAsync(repo.GetProjectByName(name)!, dst, percent: 100, full: true, ct);
        Log.Info($"self-test verify checked={result.Checked} failures={result.Failures.Count}");
        if (result.Failures.Any())
        {
            if (!s.Quiet) AnsiConsole.MarkupLine($"[red]Verify failed[/]: {result.Failures.Count} issues");
            return 2;
        }
        if (!s.Quiet) AnsiConsole.MarkupLine("[green]Verify OK[/] — all files matched");

        repo.DeleteProjectCascade(name);
        if (!s.Quiet)
            AnsiConsole.MarkupLine($"[grey]Cleanup[/]: removed project metadata; files remain under {Markup.Escape(tmpRoot)}");

        Log.Info("self-test done ok");
        return 0;
    }
}

// ===== CLI: history =====
sealed class HistorySettings : CommandSettings
{
    [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
    [CommandOption("--db")] public string? Db { get; init; }
    [CommandOption("--json")] public bool Json { get; init; } = false;
    [CommandOption("--limit")] public int? Limit { get; init; }
}
sealed class HistoryCommand : AsyncCommand<HistorySettings>
{
  public override Task<int> ExecuteAsync(CommandContext context, HistorySettings s, CancellationToken ct)
{
    var db = ConfigHelper.ResolveDb(s.Db);
    var repo = new SqliteRepository(db);
    repo.EnsureSchema();

    var proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");

    var snaps = repo.GetSnapshotsForProject(proj.Name);
    if (s.Limit is int lim && lim > 0)
        snaps = snaps.Take(lim);

    var list = snaps.ToList(); // materialize once

    Log.Info($"history name={proj.Name} count={list.Count} json={s.Json}");

    if (s.Json)
    {
        var json = JsonSerializer.Serialize(
            list.Select(x => new {
                x.Id,
                CreatedUtc = x.CreatedUtc.ToString("u"),
                x.FileCount,
                x.TotalBytes
            }),
            new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        return Task.FromResult(0);
    }

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Snapshot");
    table.AddColumn("Created (UTC)");
    table.AddColumn(new TableColumn("Files").RightAligned());
    table.AddColumn(new TableColumn("Bytes").RightAligned());

    foreach (var srow in list)
        table.AddRow(
            srow.Id.ToString(),
            srow.CreatedUtc.ToString("u"),
            srow.FileCount.ToString(),
            srow.TotalBytes.ToString("N0"));

    AnsiConsole.MarkupLine($"History for [bold]{Markup.Escape(proj.Name)}[/] — {list.Count} snapshot(s)");
    AnsiConsole.Write(table);
    return Task.FromResult(0);
}
}

// ===== CLI: diff =====
sealed class DiffSettings : CommandSettings
{
    [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
    [CommandArgument(1, "[A]")] public int? A { get; init; }
    [CommandArgument(2, "[B]")] public int? B { get; init; }
    [CommandOption("--db")] public string? Db { get; init; }
    [CommandOption("--limit")] public int Limit { get; init; } = 200; // cap printed rows
    [CommandOption("--json")] public bool Json { get; init; } = false;
}
sealed class DiffCommand : AsyncCommand<DiffSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, DiffSettings s, CancellationToken ct)
{
    var db = ConfigHelper.ResolveDb(s.Db);
    var repo = new SqliteRepository(db);
    repo.EnsureSchema();

    var proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");

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
        var idx = snaps.FindIndex(x => x.Id == aId);
        if (idx < 0 || idx + 1 >= snaps.Count)
            throw new Exception("Cannot infer the other snapshot; provide both A and B.");
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

    foreach (var kv in aFiles)
    {
        var rel = kv.Key;
        var af = kv.Value;

        if (!bFiles.TryGetValue(rel, out var bf))
        {
            added.Add(rel);
            continue;
        }
        if (!string.Equals(af.HashSha256, bf.HashSha256, StringComparison.OrdinalIgnoreCase) || af.Size != bf.Size)
            modified.Add(rel);
        else
            unchanged.Add(rel);
    }
    foreach (var kv in bFiles)
    {
        var rel = kv.Key;
        if (!aFiles.ContainsKey(rel))
            deleted.Add(rel);
    }

    Log.Info($"diff name={proj.Name} A={aId} B={bId} added={added.Count} deleted={deleted.Count} modified={modified.Count} unchanged={unchanged.Count} json={s.Json}");

    if (s.Json)
    {
        var json = JsonSerializer.Serialize(new {
            A = aId, B = bId,
            added, deleted, modified, unchanged,
            summary = new {
                added = added.Count, deleted = deleted.Count,
                modified = modified.Count, unchanged = unchanged.Count,
                totalA = aFiles.Count, totalB = bFiles.Count
            }
        }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        return Task.FromResult(0);
    }

    AnsiConsole.MarkupLine($"Diff [bold]{Markup.Escape(proj.Name)}[/] — A: {aId} vs B: {bId}");
    var grid = new Grid().AddColumn().AddColumn().AddColumn().AddColumn();
    grid.AddRow(
        $"[green]Added[/]: {added.Count}",
        $"[red]Deleted[/]: {deleted.Count}",
        $"[yellow]Modified[/]: {modified.Count}",
        $"[grey]Unchanged[/]: {unchanged.Count}");
    AnsiConsole.Write(grid);

    void PrintList(string title, string color, IEnumerable<string> rows)
    {
        var total = rows is ICollection<string> c ? c.Count : rows.Count();
        var list  = rows.Take(s.Limit).ToList();
        if (list.Count == 0) return;

        var table = new Table().Border(TableBorder.Rounded);
        table.Title = new TableTitle($"[{color}]{title}[/] (showing {list.Count}{(total > list.Count ? $"/{total}" : "")})");
        table.AddColumn("Path");
        foreach (var r in list) table.AddRow(r);
        AnsiConsole.Write(table);
    }

    PrintList("ADDED", "green", added);
    PrintList("DELETED", "red", deleted);
    PrintList("MODIFIED", "yellow", modified);

    return Task.FromResult(0);
}
}

// ===== CLI: add-project =====
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

        var id = repo.AddProject(new Project
        {
            Name = s.Name,
            RootPath = fullPath,
            Preset = s.Preset
        });

        if (!s.Quiet)
            AnsiConsole.MarkupLine(
                $"[green]Added[/] project [bold]{Markup.Escape(s.Name)}[/] (id {id}) → {Markup.Escape(fullPath)} [grey](preset: {Markup.Escape(s.Preset)})[/]"
            );

        return Task.FromResult(0);
    }
}

// ===== CLI: remove-project =====
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
        if (proj is null)
            throw new Exception($"Project '{s.Name}' not found");

        if (!s.Yes && !s.Quiet)
        {
            var confirm = AnsiConsole.Confirm($"Delete project [bold]{Markup.Escape(s.Name)}[/] and all its snapshots/files?");
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[yellow]Aborted[/]");
                return Task.FromResult(1);
            }
        }

        var stats = repo.DeleteProjectCascade(s.Name);
        if (stats.Projects == 0)
            throw new Exception($"Project '{s.Name}' not found (nothing deleted)");

        if (!s.Quiet)
            AnsiConsole.MarkupLine($"[green]Removed[/] project [bold]{Markup.Escape(s.Name)}[/] — Snapshots: {stats.Snapshots}, Files: {stats.Files}");

        return Task.FromResult(0);
    }
}

// ===== CLI: set-path =====
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
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(full);

        if (!repo.UpdateProjectPath(s.Name, full, out var oldPath))
            throw new Exception($"Project '{s.Name}' not found");

        if (!s.Quiet)
            AnsiConsole.MarkupLine($"[green]Updated[/] [bold]{Markup.Escape(s.Name)}[/] path: {Markup.Escape(oldPath ?? "?")} → {Markup.Escape(full)}");

        return Task.FromResult(0);
    }
}

// ===== CLI: list-projects =====
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
                r.Name,
                r.RootPath,
                r.Preset,
                CreatedUtc = r.CreatedUtc.ToString("u")
            }), options));
            return Task.FromResult(0);
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn(new TableColumn("Path").NoWrap());
        table.AddColumn("Preset");
        table.AddColumn("Created (UTC)");

        foreach (var p in rows)
            table.AddRow(p.Name, p.RootPath, p.Preset, p.CreatedUtc.ToString("u"));

        AnsiConsole.Write(table);
        return Task.FromResult(0);
    }
}

// ===== CLI: snapshot =====
sealed class SnapshotSettings : CommandSettings
{
    [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
    [CommandOption("--full-hash")] public bool FullHash { get; init; } = true;
    [CommandOption("--db")] public string? Db { get; init; }
    [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
}

// ===== CLI: restore =====
// ===== CLI: restore =====
// ===== CLI: restore =====
sealed class RestoreSettings : CommandSettings
{
    [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
    [CommandArgument(1, "<destination>")] public string Destination { get; init; } = "";
    [CommandOption("--snapshot")] public int? Snapshot { get; init; }
    [CommandOption("--dry-run")] public bool DryRun { get; init; } = false;
    [CommandOption("--clean")] public bool Clean { get; init; } = false;
    [CommandOption("--keep-empty-dirs")] public bool KeepEmptyDirs { get; init; } = false; // NEW
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

    // Resolve snapshot (+ capture timestamp for UX/JSON)
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

    // Load file list for the chosen snapshot
    var snapFiles = repo.GetFilesForSnapshot(snapshotId).ToList();
    if (!snapFiles.Any())
        throw new Exception($"Snapshot {snapshotId} has no files");

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var destRoot = s.Destination.Replace("~", home);
    var started = DateTime.UtcNow;

    // Safety: prevent cleaning inside the project root
    var projRootFull = Path.GetFullPath(proj.RootPath);
    var destRootFull = Path.GetFullPath(destRoot);
    if (s.Clean && destRootFull.StartsWith(projRootFull, StringComparison.OrdinalIgnoreCase))
        throw new Exception("Refusing to --clean a destination that is inside the project root.");

    Directory.CreateDirectory(destRootFull);

    // Build target set (rel paths expected at destination)
    var targetRelSet = new HashSet<string>(snapFiles.Select(f => f.RelPath), StringComparer.OrdinalIgnoreCase);

    // CLEAN (remove files not present in snapshot)
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

    // COPY snapshot contents to destination
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

    // Optional: prune empty directories after cleaning/copying
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

    // Output
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

    // Non-zero if anything was missing (useful in CI)
    return skippedMissing > 0 ? 1 : 0;
}
}
sealed class SnapshotCommand : AsyncCommand<SnapshotSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SnapshotSettings s, CancellationToken ct)
{
    var db = ConfigHelper.ResolveDb(s.Db);
    var repo = new SqliteRepository(db);
    repo.EnsureSchema();

    var proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");

    var hash = new HashService();
    var svc = new SnapshotService(repo, hash);

    Log.Info($"snapshot start name={proj.Name} fullHash={s.FullHash} root={proj.RootPath}");

    if (!s.Quiet)
        AnsiConsole.MarkupLine($"[blue]Scanning & hashing[/] {Markup.Escape(proj.Name)} at {Markup.Escape(proj.RootPath)} (preset: {Markup.Escape(proj.Preset)})…");

    var started = DateTime.UtcNow;
    var snapId = await svc.CreateSnapshotAsync(proj, s.FullHash, ct);
    var took = DateTime.UtcNow - started;
    var outcome = SnapshotService.LastOutcome;

    if (!s.Quiet)
    {
        AnsiConsole.MarkupLine($"[green]Snapshot {snapId} created[/] in {took.TotalSeconds:F1}s");
        if (outcome is not null)
        {
            AnsiConsole.MarkupLine($"[grey]Added[/]: {outcome.Added}, [grey]Modified[/]: {outcome.Modified}, [grey]Deleted[/]: {outcome.Deleted}, [grey]Unchanged[/]: {outcome.Unchanged}, [grey]Total files[/]: {outcome.TotalFiles}, [grey]Bytes[/]: {outcome.TotalBytes}");
        }
    }

    Log.Info($"snapshot done id={snapId} took={took.TotalMilliseconds:F0}ms added={outcome?.Added} modified={outcome?.Modified} deleted={outcome?.Deleted} unchanged={outcome?.Unchanged} total={outcome?.TotalFiles} bytes={outcome?.TotalBytes}");
    return 0;
}
}

// ===== CLI: sync =====
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

    Log.Info($"sync start name={proj.Name} dry={s.DryRun} src={proj.RootPath} dest={dest}");

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
        if (code == 0)
            AnsiConsole.MarkupLine($"[green]Sync complete[/] in {took.TotalSeconds:F1}s (exit 0)");
        else
            AnsiConsole.MarkupLine($"[red]Sync failed[/] (exit {code})");
    }

    Log.Info($"sync done exit={code} took={took.TotalMilliseconds:F0}ms");
    return code;
}
}

// ===== CLI: verify =====
// ===== CLI: verify =====
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


// ===== CLI: presets list/show =====
sealed class PresetsListSettings : CommandSettings { }

sealed class PresetsListCommand : AsyncCommand<PresetsListSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, PresetsListSettings s, CancellationToken ct)
    {
        var names = PresetStore.ListNames().ToList();
        if (names.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No presets found[/]");
            return Task.FromResult(0);
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Preset");
        foreach (var n in names) table.AddRow(n);
        AnsiConsole.Write(table);
        return Task.FromResult(0);
    }
}

sealed class PresetsShowSettings : CommandSettings
{
    [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
}

sealed class PresetsShowCommand : AsyncCommand<PresetsShowSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, PresetsShowSettings s, CancellationToken ct)
    {
        var content = PresetStore.Load(s.Name);
        AnsiConsole.MarkupLine($"[blue]{Markup.Escape(s.Name)}[/] preset:");
        AnsiConsole.WriteLine(content);
        return Task.FromResult(0);
    }
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
// ===== CLI: doctor =====
sealed class DoctorSettings : CommandSettings
{
    [CommandOption("--db")] public string? Db { get; init; }
    [CommandOption("--check-dest <PATH>")] public string? CheckDest { get; init; }
    [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
}

sealed class DoctorCommand : AsyncCommand<DoctorSettings>
{
public override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings s, CancellationToken ct)
{
    var ok = true;
    void Pass(string msg) { if (!s.Quiet) AnsiConsole.MarkupLine($"[green]✔[/] {Markup.Escape(msg)}"); }
    void Fail(string msg) { ok = false; if (!s.Quiet) AnsiConsole.MarkupLine($"[red]✘[/] {Markup.Escape(msg)}"); }

    // 1) Platform sync tool check
    try
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // robocopy
            var p = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "robocopy",
                ArgumentList = { "/?" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(p)!;
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode <= 16) Pass("robocopy found (Windows sync runner)");
            else Fail("robocopy returned unexpected exit");
        }
        else
        {
            // rsync
            var p = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rsync",
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(p)!;
            var txt = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode == 0 && txt.Contains("rsync", StringComparison.OrdinalIgnoreCase))
                Pass("rsync found (Unix sync runner)");
            else
                Fail("rsync not available or returned non-zero");
        }
    }
    catch { Fail("Platform sync tool not found on PATH"); }

    // 2) DB path & write check
    try
    {
        var db = ConfigHelper.ResolveDb(s.Db);
        var dir = Path.GetDirectoryName(db)!;
        Directory.CreateDirectory(dir);
        var testFile = Path.Combine(dir, ".vaultsync_write_test");
        await File.WriteAllTextAsync(testFile, "ok", ct);
        File.Delete(testFile);
        Pass($"Database path writable: {db}");
    }
    catch (Exception ex)
    {
        Fail($"Database path not writable: {ex.Message}");
    }

    // 3) Project paths exist (if any)
    try
    {
        var db = ConfigHelper.ResolveDb(s.Db);
        var repo = new SqliteRepository(db);
        repo.EnsureSchema();
        var projects = repo.ListProjects();
        if (!projects.Any())
        {
            if (!s.Quiet) AnsiConsole.MarkupLine("[yellow]No projects registered yet[/]");
        }
        foreach (var p in projects)
        {
            if (Directory.Exists(p.RootPath)) Pass($"Project path exists: {p.Name} → {p.RootPath}");
            else Fail($"Project path missing: {p.Name} → {p.RootPath}");
        }
    }
    catch (Exception ex)
    {
        Fail($"Could not inspect projects: {ex.Message}");
    }

    // 4) Optional dest write check
    if (!string.IsNullOrWhiteSpace(s.CheckDest))
    {
        try
        {
            var dest = s.CheckDest.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Directory.CreateDirectory(dest);
            var test = Path.Combine(dest, ".vaultsync_write_test");
            await File.WriteAllTextAsync(test, "ok", ct);
            File.Delete(test);
            Pass($"Destination writable: {dest}");
        }
        catch (Exception ex)
        {
            Fail($"Destination not writable: {ex.Message}");
        }
    }

    if (!s.Quiet)
        AnsiConsole.MarkupLine(ok ? "[green]Doctor: all good[/]" : "[red]Doctor: issues found[/]");

    return ok ? 0 : 2;
}}

// ===== CLI: version =====
sealed class VersionSettings : CommandSettings { }
sealed class VersionCommand : AsyncCommand<VersionSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, VersionSettings s, CancellationToken ct)
    {
        var asm = Assembly.GetExecutingAssembly();
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational ?? asm.GetName().Version?.ToString() ?? "0.0.0";
        AnsiConsole.MarkupLine($"VaultSync CLI v{Markup.Escape(version)}");
        return Task.FromResult(0);
    }
}

// ===== CLI: config branch =====
sealed class ConfigShowSettings : CommandSettings { }
sealed class ConfigPathSettings : CommandSettings { }

sealed class ConfigShowCommand : AsyncCommand<ConfigShowSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ConfigShowSettings settings, CancellationToken ct)
    {
        var cfg = ConfigHelper.Load();
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        return Task.FromResult(0);
    }
}

sealed class ConfigPathCommand : AsyncCommand<ConfigPathSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ConfigPathSettings settings, CancellationToken ct)
    {
        var cfg = ConfigHelper.Load();
        Console.WriteLine(cfg.Database);
        return Task.FromResult(0);
    }
}

sealed class ConfigSetDbSettings : CommandSettings
{
    [CommandArgument(0, "<dbPath>")] public string DbPath { get; init; } = "";
}

sealed class ConfigSetDbCommand : AsyncCommand<ConfigSetDbSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ConfigSetDbSettings s, CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expanded = s.DbPath.Replace("~", home);

        // Validate folder exists or can be created
        var dir = Path.GetDirectoryName(expanded)!;
        Directory.CreateDirectory(dir);

        var cfg = ConfigHelper.Load();
        cfg.Database = s.DbPath; // store with ~ if user passed it
        ConfigHelper.Save(cfg);

        AnsiConsole.MarkupLine($"[green]Updated[/] config: database → {Markup.Escape(s.DbPath)}");
        return Task.FromResult(0);
    }
}

static class PresetStore
{
    private static string PresetsDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".vaultsync", "presets");
    }

    public static IEnumerable<string> ListNames()
    {
        // User-defined
        var dir = PresetsDir();
        Directory.CreateDirectory(dir);
        var userFiles = Directory.EnumerateFiles(dir, "*.vaultsyncignore")
                                 .Select(f => Path.GetFileNameWithoutExtension(f))
                                 .Select(n => n); // already without extension
        // Built-ins
        var builtIns = BuiltIn().Keys;

        // Union (user overrides may shadow built-ins of the same name)
        return userFiles.Union(builtIns, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
    }

    public static string Load(string name)
    {
        // 1) User override
        var userPath = Path.Combine(PresetsDir(), $"{name}.vaultsyncignore");
        if (File.Exists(userPath))
            return File.ReadAllText(userPath);

        // 2) Built-in
        var built = BuiltIn();
        if (built.TryGetValue(name, out var content))
            return content;

        throw new Exception($"Preset '{name}' not found. Put a file at {userPath} or use a built-in: {string.Join(", ", built.Keys.OrderBy(k => k))}");
    }

    private static Dictionary<string,string> BuiltIn()
    {
        // Minimal seeds (extend freely)
        var unity = string.Join('\n', new[]
        {
            "Library/",
            "Temp/",
            "Obj/",
            "Build/",
            "Builds/",
            "Logs/",
            "*.csproj",
            "*.sln",
            "*.user",
            "*.unitypackage"
        });

        var dotnet = string.Join('\n', new[]
        {
            "bin/",
            "obj/",
            "*.user",
            "*.suo",
            "*.userprefs",
            ".vs/",
        });

        var blender = string.Join('\n', new[]
        {
            "*.blend1",
            "*.blend2",
            "*.blend@([0-9])",
            "*.blend@([0-9][0-9])",
            "__pycache__/",
            "*.pyc"
        });

        return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["unity"]  = unity + "\n",
            ["dotnet"] = dotnet + "\n",
            ["blender"]= blender + "\n"
        };
    }
}

// === Watch command settings ===
public sealed class WatchSettings : CommandSettings
{
    [CommandArgument(0, "<ProjectName>")]
    public string ProjectName { get; init; } = default!;

    [CommandOption("--dest <DEST_PATH>")]
    [Description("Optional destination for sync after each snapshot.")]
    public string? Destination { get; init; }

    [CommandOption("--debounce-ms <MILLISECONDS>")]
    [Description("Debounce window to coalesce rapid file change events (default 750ms).")]
    public int DebounceMs { get; init; } = 750;

    [CommandOption("--sync")]
    [Description("After each snapshot, run sync to DEST (requires --dest).")]
    public bool Sync { get; init; }

    [CommandOption("--verify")]
    [Description("After sync, run full verify to DEST (implies --sync).")]
    public bool Verify { get; init; }

    [CommandOption("--dry-run")]
    [Description("Use rsync --dry-run for sync step (no files actually written).")]
    public bool DryRun { get; init; }

    [CommandOption("--quiet")]
    [Description("Minimize console output from child operations.")]
    public bool Quiet { get; init; }
}

// === Simple async debouncer ===
file sealed class AsyncDebouncer
{
    private readonly int _delayMs;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public AsyncDebouncer(int delayMs) => _delayMs = Math.Max(0, delayMs);

    public void Trigger(Func<CancellationToken, Task> work)
    {
        CancellationTokenSource? toCancel;
        lock (_gate)
        {
            toCancel = _cts;
            _cts = new CancellationTokenSource();
            toCancel?.Cancel();
        }

        var localCts = _cts!;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_delayMs, localCts.Token);
                await work(localCts.Token);
            }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]watch error:[/] {Markup.Escape(ex.Message)}");
            }
        });
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }
}

// === Watch command ===
// === Watch command ===
public sealed class WatchCommand : AsyncCommand<WatchSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, WatchSettings s, CancellationToken ct)
    {
        // Resolve DB + repo
        var db = ConfigHelper.ResolveDb(null);
        var repo = new SqliteRepository(db);
        repo.EnsureSchema();

        // Resolve project
        var proj = repo.GetProjectByName(s.ProjectName);
        if (proj is null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Project '{Markup.Escape(s.ProjectName)}' not found");
            return 2;
        }

        var root = proj.RootPath;
        if (!Directory.Exists(root))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Project path not found: {Markup.Escape(root)}");
            return 2;
        }

        // verify implies sync; compute effective flags
        var doVerify = s.Verify;
        var doSync = s.Sync || doVerify;

        if (doSync && string.IsNullOrWhiteSpace(s.Destination))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --sync/--verify requires --dest");
            return 2;
        }

        AnsiConsole.MarkupLine($"[grey]Watching[/] {Markup.Escape(root)} [grey](preset: {proj.Preset})[/]");
        if (doSync)
        {
            var extra = s.DryRun ? " (dry-run)" : "";
            var tail = doVerify ? " and verify" : "";
            AnsiConsole.MarkupLine($"[grey]→ will sync to[/] {Markup.Escape(s.Destination!)}[grey]{extra}{tail}[/]");
        }

        // One full cycle: snapshot -> optional sync -> optional verify
        async Task RunCycle(CancellationToken token, string reason)
        {
            if (token.IsCancellationRequested) return;

            if (!s.Quiet)
                AnsiConsole.MarkupLine($"[dim]• change detected ({Markup.Escape(reason)}); snapshotting…[/]");

            var snapSvc = new SnapshotService(repo, new HashService());
            var snapId = await snapSvc.CreateSnapshotAsync(proj, fullHash: true, token);

            if (!s.Quiet)
            {
                var outcome = SnapshotService.LastOutcome;
                if (outcome is not null)
                {
                    AnsiConsole.MarkupLine(
                        $"[green]Snapshot {snapId}[/] " +
                        $"Added: {outcome.Added}, Modified: {outcome.Modified}, Deleted: {outcome.Deleted}, " +
                        $"Unchanged: {outcome.Unchanged}, Bytes: {outcome.TotalBytes}");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[green]Snapshot {snapId} created[/]");
                }
            }

            if (!doSync) return;

            var dest = s.Destination!.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var syncSvc = new SyncService();
            var code = await syncSvc.SyncAsync(proj, dest, s.DryRun, token);
            if (code != 0)
            {
                AnsiConsole.MarkupLine($"[red]Sync failed[/] (exit {code})");
                return;
            }
            if (!s.Quiet) AnsiConsole.MarkupLine("[green]Sync complete[/]");

            if (doVerify)
            {
                var verifySvc = new VerifyService(repo, new HashService());
                var vr = await verifySvc.VerifyAsync(proj, dest, percent: 100, full: true, token);
                if (vr.Failures.Any())
                    AnsiConsole.MarkupLine($"[red]Verify failed:[/] {vr.Failures.Count} issue(s)");
                else if (!s.Quiet)
                    AnsiConsole.MarkupLine("[green]Verify OK[/]");
            }
        }

        // Initial run at startup
        await RunCycle(ct, "startup");

        // Watcher + debounce
        using var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size
        };

        var debouncer = new AsyncDebouncer(Math.Max(100, s.DebounceMs));
        void OnChange(object? _, FileSystemEventArgs e)
            => debouncer.Trigger(t => RunCycle(t, $"{e.ChangeType}: {e.FullPath}"));

        void OnRename(object? _, RenamedEventArgs e)
            => debouncer.Trigger(t => RunCycle(t, $"Renamed: {e.OldFullPath} → {e.FullPath}"));

        void OnError(object? _, ErrorEventArgs e)
            => AnsiConsole.MarkupLine($"[red]watch error:[/] {Markup.Escape(e.GetException().Message)}");

        watcher.Changed += OnChange;
        watcher.Created += OnChange;
        watcher.Deleted += OnChange;
        watcher.Renamed += OnRename;
        watcher.Error += OnError;

        AnsiConsole.MarkupLine("[grey]Press Ctrl+C to stop.[/]");
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (sender, ea) =>
        {
            ea.Cancel = true;
            tcs.TrySetResult();
        };
        await tcs.Task;

        debouncer.Cancel();
        watcher.EnableRaisingEvents = false;
        return 0;
    }
}
// ===== CLI: prune =====
sealed class PruneSettings : CommandSettings
{
    [CommandArgument(0, "<name>")] public string Name { get; init; } = "";

    // Keep newest N snapshots (delete the rest)
    [CommandOption("--keep-last <N>")] public int? KeepLast { get; init; }

    // Delete snapshots created strictly before this UTC date (YYYY-MM-DD)
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
        if (!string.IsNullOrWhiteSpace(Before))
        {
            if (!DateTime.TryParse(Before, out _))
                return ValidationResult.Error("--before must be a date like 2025-11-08");
        }
        return ValidationResult.Success();
    }
}

sealed class PruneCommand : AsyncCommand<PruneSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, PruneSettings s, CancellationToken ct)
    {
        var db = ConfigHelper.ResolveDb(s.Db);
        var repo = new SqliteRepository(db);
        repo.EnsureSchema();

        var proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");

        var snaps = repo.GetSnapshotsForProject(proj.Name).ToList(); // newest first (assumed)
        if (snaps.Count == 0)
        {
            if (!s.Quiet) AnsiConsole.MarkupLine("[yellow]No snapshots to prune[/].");
            return Task.FromResult(0);
        }

        var toDelete = new List<int>();

        if (s.KeepLast is int keep)
        {
            // keep newest <keep>; delete the rest
            if (snaps.Count > keep)
                toDelete.AddRange(snaps.Skip(keep).Select(x => x.Id));
        }
        else if (!string.IsNullOrWhiteSpace(s.Before))
        {
            var cutoff = DateTime.Parse(s.Before!).ToUniversalTime().Date; // start of that day UTC
            toDelete.AddRange(snaps.Where(x => x.CreatedUtc < cutoff).Select(x => x.Id));
        }

        // summarize
        var planned = toDelete.Distinct().OrderBy(id => id).ToList();

        if (s.Json)
        {
            var payload = new
            {
                project = proj.Name,
                totalSnapshots = snaps.Count,
                plannedDeletions = planned,
                dryRun = s.DryRun
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (!s.Quiet)
        {
            AnsiConsole.MarkupLine($"Prune [bold]{Markup.Escape(proj.Name)}[/]: total snapshots {snaps.Count}");
            if (planned.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]Nothing to delete[/].");
            }
            else
            {
                var tbl = new Table().Border(TableBorder.Rounded);
                tbl.AddColumn("Snapshot");
                tbl.AddColumn("Created (UTC)");
                tbl.AddColumn(new TableColumn("Files").RightAligned());
                tbl.AddColumn(new TableColumn("Bytes").RightAligned());

                var byId = snaps.ToDictionary(x => x.Id);
                foreach (var id in planned)
                {
                    var srow = byId[id];
                    tbl.AddRow(id.ToString(), srow.CreatedUtc.ToString("u"), srow.FileCount.ToString(), srow.TotalBytes.ToString("N0"));
                }
                AnsiConsole.Write(tbl);
                if (s.DryRun)
                    AnsiConsole.MarkupLine("[yellow]Dry run[/]: no changes written.");
            }
        }

        if (planned.Count == 0 || s.DryRun)
            return Task.FromResult(0);

        // Perform deletion
        var stats = repo.DeleteSnapshotsById(proj.Name, planned);
        if (!s.Quiet && !s.Json)
            AnsiConsole.MarkupLine($"[green]Pruned[/] snapshots: {stats.Snapshots}, files: {stats.Files}");

        return Task.FromResult(0);
    }
}

// ===== Program entry =====
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Init(); // <-- start file logging

        try
        {
            var app = new CommandApp();
            app.Configure(cfg =>
            {
                cfg.AddCommand<PruneCommand>("prune")
                    .WithDescription("Delete old snapshots by count (--keep-last) or date (--before). Supports --dry-run.");

                cfg.AddCommand<SetPathCommand>("set-path")
                    .WithDescription("Update a project's root path");

                cfg.AddCommand<SetPathCommand>("update-path")
                    .WithDescription("Alias of set-path: update a project's root path");

                cfg.AddCommand<RestoreCommand>("restore")
                   .WithDescription("Restore a snapshot (latest by default) into a destination folder");

                cfg.AddCommand<HistoryCommand>("history")
                   .WithDescription("Show snapshot history for a project");

                cfg.AddCommand<DiffCommand>("diff")
                   .WithDescription("Compare two snapshots (default: latest vs previous)");

                cfg.AddCommand<SelfTestCommand>("self-test")
                   .WithDescription("Run an end-to-end smoke test (temp project → snapshot → sync → verify)");

                cfg.SetApplicationName("vaultsync");

                cfg.AddCommand<InitCommand>("init")
                   .WithDescription("Initialize local config & database path");

                cfg.AddCommand<AddProjectCommand>("add-project")
                   .WithDescription("Register a folder with a preset");

                cfg.AddCommand<RemoveProjectCommand>("remove-project")
                   .WithDescription("Remove a project (and all snapshots/files)");

                cfg.AddCommand<ListProjectsCommand>("list-projects")
                   .WithDescription("List registered projects");

                cfg.AddCommand<SnapshotCommand>("snapshot")
                   .WithDescription("Create a snapshot (scan + hash) of a project");

                cfg.AddCommand<SyncCommand>("sync")
                    .WithDescription("Mirror a project folder to a destination (rsync/robocopy)");
                    
                cfg.AddCommand<VerifyCommand>("verify")
                   .WithDescription("Verify destination matches latest snapshot (sample or full)");

                cfg.AddCommand<WatchCommand>("watch")
                    .WithDescription("Watch a project for file changes and auto-snapshot/sync/verify");
                cfg.AddCommand<DoctorCommand>("doctor")
                   .WithDescription("Check environment: rsync, DB path, project paths, and optional destination writability");

                cfg.AddCommand<VersionCommand>("version")
                   .WithDescription("Show version");

                cfg.AddBranch("config", b =>
                {
                    b.SetDescription("Inspect and modify VaultSync configuration");
                    b.AddCommand<ConfigShowCommand>("show").WithDescription("Print full JSON config");
                    b.AddCommand<ConfigPathCommand>("path").WithDescription("Print the configured database path");
                    b.AddCommand<ConfigSetDbCommand>("set-db").WithDescription("Set database path (e.g., set-db ~/.vaultsync/vault.db)");
                });

                cfg.AddBranch("presets", b =>
                {
                    b.SetDescription("View preset names and contents");
                    b.AddCommand<PresetsListCommand>("list").WithDescription("List available presets");
                    b.AddCommand<PresetsShowCommand>("show").WithDescription("Show the content of a preset");
                });

            });

            Log.Info($"argv: {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
            var code = await app.RunAsync(args);
            Log.Info($"exit: {code}");
            return code;
        }
        catch (Exception ex)
        {
            Log.Exception(ex, "Main");
            // Keep Spectre error output behavior, but make sure we logged it.
            throw;
        }
    }
}
