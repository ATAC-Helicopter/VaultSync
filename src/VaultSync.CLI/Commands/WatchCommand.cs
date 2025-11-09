using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Config;
using VaultSync.CLI.Utils;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;

namespace VaultSync.CLI.Commands
{
    public sealed class WatchSettings : CommandSettings
    {
        [CommandArgument(0, "<ProjectName>")]
        public string ProjectName { get; init; } = default!;

        [CommandOption("--dest <DEST_PATH>")]
        public string? Destination { get; init; }

        [CommandOption("--debounce-ms <MILLISECONDS>")]
        public int DebounceMs { get; init; } = 750;

        [CommandOption("--sync")]
        public bool Sync { get; init; }

        [CommandOption("--verify")]
        public bool Verify { get; init; }

        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }

        [CommandOption("--quiet")]
        public bool Quiet { get; init; }
    }

    public sealed class WatchCommand : AsyncCommand<WatchSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, WatchSettings s, CancellationToken ct)
        {
            var db = ConfigHelper.ResolveDb(null);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

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

            await RunCycle(ct, "startup");

            using var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
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
}