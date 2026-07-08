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
        private static readonly System.Threading.SemaphoreSlim _cycleGate = new(1, 1);
        private sealed record WatchPlan(Core.Models.Project Project, bool DoSync, bool DoVerify);

        protected override async Task<int> ExecuteAsync(CommandContext context, WatchSettings s, CancellationToken cancellationToken)
        {
            string db = ConfigHelper.ResolveDb(null);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            WatchPlan? plan = CreateWatchPlan(repo, s);
            if (plan is null)
                return 2;

            WriteWatchPlan(plan, s);
            await RunCycleAsync(repo, plan, s, cancellationToken, "startup");

            using var watcher = new FileSystemWatcher(plan.Project.RootPath)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            var debouncer = new AsyncDebouncer(Math.Max(100, s.DebounceMs));
            AttachHandlers(watcher, debouncer, repo, plan, s, cancellationToken);

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

        private static WatchPlan? CreateWatchPlan(SqliteRepository repo, WatchSettings settings)
        {
            Core.Models.Project? project = repo.GetProjectByName(settings.ProjectName);
            if (project is null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Project '{Markup.Escape(settings.ProjectName)}' not found");
                return null;
            }

            if (!Directory.Exists(project.RootPath))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Project path not found: {Markup.Escape(project.RootPath)}");
                return null;
            }

            bool doVerify = settings.Verify;
            bool doSync = settings.Sync || doVerify;
            if (doSync && string.IsNullOrWhiteSpace(settings.Destination))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] --sync/--verify requires --dest");
                return null;
            }

            return new WatchPlan(project, doSync, doVerify);
        }

        private static void WriteWatchPlan(WatchPlan plan, WatchSettings settings)
        {
            AnsiConsole.MarkupLine($"[grey]Watching[/] {Markup.Escape(plan.Project.RootPath)} [grey](preset: {plan.Project.Preset})[/]");
            if (!plan.DoSync)
                return;

            string extra = settings.DryRun ? " (dry-run)" : "";
            string tail = plan.DoVerify ? " and verify" : "";
            AnsiConsole.MarkupLine($"[grey]-> will sync to[/] {Markup.Escape(settings.Destination!)}[grey]{extra}{tail}[/]");
        }

        private static void AttachHandlers(
            FileSystemWatcher watcher,
            AsyncDebouncer debouncer,
            SqliteRepository repo,
            WatchPlan plan,
            WatchSettings settings,
            CancellationToken cancellationToken)
        {
            watcher.Changed += (_, e) => QueueCycle(debouncer, repo, plan, settings, cancellationToken, $"{e.ChangeType}: {e.FullPath}");
            watcher.Created += (_, e) => QueueCycle(debouncer, repo, plan, settings, cancellationToken, $"{e.ChangeType}: {e.FullPath}");
            watcher.Deleted += (_, e) => QueueCycle(debouncer, repo, plan, settings, cancellationToken, $"{e.ChangeType}: {e.FullPath}");
            watcher.Renamed += (_, e) => QueueCycle(debouncer, repo, plan, settings, cancellationToken, $"Renamed: {e.OldFullPath} -> {e.FullPath}");
            watcher.Error += (_, e) => AnsiConsole.MarkupLine($"[red]watch error:[/] {Markup.Escape(e.GetException().Message)}");
        }

        private static void QueueCycle(
            AsyncDebouncer debouncer,
            SqliteRepository repo,
            WatchPlan plan,
            WatchSettings settings,
            CancellationToken cancellationToken,
            string reason)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            debouncer.Trigger(t => RunCycleAsync(repo, plan, settings, t, reason));
        }

        private static async Task RunCycleAsync(
            SqliteRepository repo,
            WatchPlan plan,
            WatchSettings settings,
            CancellationToken token,
            string reason)
        {
            await _cycleGate.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested)
                    return;

                if (!settings.Quiet)
                    AnsiConsole.MarkupLine($"[dim]* change detected ({Markup.Escape(reason)}); snapshotting...[/]");

                await CreateSnapshotAsync(repo, plan.Project, settings.Quiet, token);
                if (plan.DoSync)
                    await SyncAndVerifyAsync(repo, plan, settings, token);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                AnsiConsole.MarkupLine("[red]Database error[/]: FOREIGN KEY constraint failed during snapshot write. This can occur if two cycles overlap. The watcher now enforces a single in-flight cycle.");
            }
            finally
            {
                _cycleGate.Release();
            }
        }

        private static async Task CreateSnapshotAsync(
            SqliteRepository repo,
            Core.Models.Project project,
            bool quiet,
            CancellationToken token)
        {
            var snapSvc = new SnapshotService(repo, new HashService());
            int snapId = await snapSvc.CreateSnapshotAsync(
                project,
                fullHash: true,
                maxSnapshotsToKeep: null,
                ct: token);

            if (!quiet)
                WriteSnapshotResult(snapId);
        }

        private static void WriteSnapshotResult(int snapId)
        {
            SnapshotOutcome? outcome = SnapshotService.LastOutcome;
            if (outcome is null)
            {
                AnsiConsole.MarkupLine($"[green]Snapshot {snapId} created[/]");
                return;
            }

            AnsiConsole.MarkupLine(
                $"[green]Snapshot {snapId}[/] " +
                $"Added: {outcome.Added}, Modified: {outcome.Modified}, Deleted: {outcome.Deleted}, " +
                $"Unchanged: {outcome.Unchanged}, Bytes: {ByteSizeFormat.FormatBytes(outcome.TotalBytes, "0.#")}");
        }

        private static async Task SyncAndVerifyAsync(
            SqliteRepository repo,
            WatchPlan plan,
            WatchSettings settings,
            CancellationToken token)
        {
            string dest = settings.Destination!.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var syncSvc = new SyncService();
            int code = await syncSvc.SyncAsync(plan.Project, dest, settings.DryRun, token);
            if (code != 0)
            {
                AnsiConsole.MarkupLine($"[red]Sync failed[/] (exit {code})");
                return;
            }

            if (!settings.Quiet)
                AnsiConsole.MarkupLine("[green]Sync complete[/]");

            if (plan.DoVerify)
                await VerifyAsync(repo, plan.Project, dest, settings.Quiet, token);
        }

        private static async Task VerifyAsync(
            SqliteRepository repo,
            Core.Models.Project project,
            string dest,
            bool quiet,
            CancellationToken token)
        {
            var verifySvc = new VerifyService(repo, new HashService());
            VerifyResult result = await verifySvc.VerifyAsync(project, dest, percent: 100, full: true, token);
            if (result.Failures.Count > 0)
                AnsiConsole.MarkupLine($"[red]Verify failed:[/] {result.Failures.Count} issue(s)");
            else if (!quiet)
                AnsiConsole.MarkupLine("[green]Verify OK[/]");
        }
    }
}
