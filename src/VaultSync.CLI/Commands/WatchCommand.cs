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

        [CommandOption("--db <PATH>")]
        public string? DbPath { get; init; }

        [CommandOption("--dest <DEST_PATH>")]
        public string? Destination { get; init; }

        [CommandOption("--debounce-ms <MILLISECONDS>")]
        public int DebounceMs { get; init; } = 750;

        [CommandOption("--sync")]
        public bool Sync { get; init; }

        [CommandOption("--verify")]
        public bool Verify { get; init; }

        [CommandOption("--dry-run")]
        [System.ComponentModel.Description("Print a finite, read-only watch plan without snapshots or transfers")]
        public bool DryRun { get; init; }

        [CommandOption("--output <FORMAT>")]
        [System.ComponentModel.Description("Plan output: text or json; JSON requires --dry-run")]
        public string? Output { get; init; }

        [CommandOption("--quiet")]
        public bool Quiet { get; init; }
    }

    public sealed class WatchCommand : AsyncCommand<WatchSettings>
    {
        private static readonly System.Threading.SemaphoreSlim _cycleGate = new(1, 1);
        private sealed record WatchPlan(Core.Models.Project Project, bool DoSync, bool DoVerify);

        protected override Task<int> ExecuteAsync(CommandContext context, WatchSettings s, CancellationToken cancellationToken)
            => RunAsync(s, cancellationToken);

        internal static async Task<int> RunAsync(WatchSettings settings, CancellationToken cancellationToken)
        {
            string? invalid = CommandOutput.Validate(settings.Output);
            if (CommandOutput.IsJson(settings.Output) && !settings.DryRun)
                invalid = "Watcher JSON is available for --dry-run plans only; live events are not supported yet.";
            if (settings.DebounceMs <= 0)
                invalid = "--debounce-ms must be positive.";
            if ((settings.Sync || settings.Verify) && string.IsNullOrWhiteSpace(settings.Destination))
                invalid = "--sync/--verify requires --dest.";
            if (invalid is not null)
                return CommandOutput.Failure(settings.Output, "watch.plan", "invalid_options", invalid, 2);
            if (settings.DryRun)
                return Preview(settings, cancellationToken);

            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler onCancel = (_, args) =>
            {
                args.Cancel = true;
                stopping.Cancel();
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                stopping.Token.ThrowIfCancellationRequested();
                string db = ConfigHelper.ResolveDb(settings.DbPath);
                var repo = new SqliteRepository(db);
                repo.EnsureSchema();
                WatchPlan? plan = CreateWatchPlan(repo, settings);
                if (plan is null)
                    return 2;

                if (!settings.Quiet)
                    WriteWatchPlan(plan, settings);
                using var watcher = new FileSystemWatcher(plan.Project.RootPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                await using var debouncer = new AsyncDebouncer(Math.Max(100, settings.DebounceMs));
                var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                AttachHandlers(watcher, debouncer, repo, plan, settings, stopping.Token, failed);
                watcher.EnableRaisingEvents = true;
                try
                {
                    // Observe source changes during the initial snapshot and transfer too.
                    // Otherwise a change made before startup finishes can be missed forever.
                    if (!await RunCycleAsync(repo, plan, settings, stopping.Token, "startup"))
                        return 2;
                    stopping.Token.ThrowIfCancellationRequested();
                    if (!settings.Quiet)
                        AnsiConsole.MarkupLine("[grey]Press Ctrl+C to stop.[/]");
                    await Task.WhenAny(Task.Delay(Timeout.Infinite, stopping.Token), failed.Task);
                    stopping.Token.ThrowIfCancellationRequested();
                    stopping.Cancel();
                    return 2;
                }
                finally
                {
                    watcher.EnableRaisingEvents = false;
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return 130;
            }
            catch (Exception error)
            {
                Log.Exception(error, "Watcher failed");
                Console.Error.WriteLine("Watcher failed. See the VaultSync CLI log for details.");
                return 2;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        private static int Preview(WatchSettings settings, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string db = ConfigHelper.ResolveDbForInspection(settings.DbPath);
                if (!File.Exists(db))
                    return CommandOutput.Failure(settings.Output, "watch.plan", "repository_unavailable",
                        "The selected database does not exist. Register the project first.", 1);
                var repo = new SqliteRepository(db, readOnly: true);
                WatchPlan? plan = CreateWatchPlan(repo, settings);
                if (plan is null)
                    return 2;
                cancellationToken.ThrowIfCancellationRequested();
                if (CommandOutput.IsJson(settings.Output))
                    return CommandOutput.Success("watch.plan", new
                    {
                        projectId = plan.Project.Id,
                        dryRun = true,
                        snapshotOnStartup = true,
                        snapshotOnChange = true,
                        mirror = plan.DoSync,
                        verify = plan.DoVerify,
                        debounceMs = Math.Max(100, settings.DebounceMs),
                        watching = false,
                        payloadChecked = false,
                        destinationChecked = false,
                        transferToolChecked = false,
                        recordedBackup = false
                    });
                if (!settings.Quiet)
                {
                    AnsiConsole.MarkupLine($"Watch plan for [bold]{Markup.Escape(plan.Project.Name)}[/]: " +
                        $"snapshot at startup and on changes; debounce {Math.Max(100, settings.DebounceMs)} ms; " +
                        $"mirror: {plan.DoSync}; verify: {plan.DoVerify}.");
                    AnsiConsole.MarkupLine("No watching, snapshots, or transfers started. Destination, transfer tools, and payload bytes were not checked.");
                }
                return 0;
            }
            catch (OperationCanceledException)
            {
                return CommandOutput.Failure(settings.Output, "watch.plan", "cancelled", "Watch planning was cancelled.", 130);
            }
            catch (Exception error)
            {
                Log.Exception(error, "Watcher planning failed");
                return CommandOutput.Failure(settings.Output, "watch.plan", "planning_failed",
                    "Watch planning failed. Check database access and supported schema; see the private CLI log.", 1);
            }
        }

        private static WatchPlan? CreateWatchPlan(SqliteRepository repo, WatchSettings settings)
        {
            Core.Models.Project? project = repo.GetProjectByName(settings.ProjectName);
            if (project is null)
            {
                CommandOutput.Failure(settings.Output, "watch.plan", "project_not_found", "No registered project matches the supplied name.", 2);
                return null;
            }

            if (!Directory.Exists(project.RootPath))
            {
                CommandOutput.Failure(settings.Output, "watch.plan", "source_unavailable", "The registered project source is unavailable.", 2);
                return null;
            }

            bool doVerify = settings.Verify;
            bool doSync = settings.Sync || doVerify;
            if (doSync && string.IsNullOrWhiteSpace(settings.Destination))
            {
                CommandOutput.Failure(settings.Output, "watch.plan", "invalid_options", "--sync/--verify requires --dest.", 2);
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
            CancellationToken cancellationToken,
            TaskCompletionSource failed)
        {
            watcher.Changed += (_, e) => QueueCycle(debouncer, repo, plan, settings, cancellationToken, failed, $"{e.ChangeType}: {e.FullPath}");
            watcher.Created += (_, e) => QueueCycle(debouncer, repo, plan, settings, cancellationToken, failed, $"{e.ChangeType}: {e.FullPath}");
            watcher.Deleted += (_, e) => QueueCycle(debouncer, repo, plan, settings, cancellationToken, failed, $"{e.ChangeType}: {e.FullPath}");
            watcher.Renamed += (_, e) => QueueCycle(debouncer, repo, plan, settings, cancellationToken, failed, $"Renamed: {e.OldFullPath} -> {e.FullPath}");
            watcher.Error += (_, e) =>
            {
                Log.Exception(e.GetException(), "Filesystem watcher failed");
                Console.Error.WriteLine("Filesystem watcher failed. See the VaultSync CLI log for details.");
                failed.TrySetResult();
            };
        }

        private static void QueueCycle(
            AsyncDebouncer debouncer,
            SqliteRepository repo,
            WatchPlan plan,
            WatchSettings settings,
            CancellationToken cancellationToken,
            TaskCompletionSource failed,
            string reason)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            debouncer.Trigger(async token =>
            {
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                try
                {
                    if (!await RunCycleAsync(repo, plan, settings, cycle.Token, reason))
                        failed.TrySetResult();
                }
                catch (OperationCanceledException) when (cycle.IsCancellationRequested)
                {
                    // A newer change or session shutdown superseded this cycle.
                }
                catch (Exception error)
                {
                    Log.Exception(error, "Watcher cycle failed");
                    Console.Error.WriteLine("Watcher cycle failed. See the VaultSync CLI log for details.");
                    failed.TrySetResult();
                }
            });
        }

        private static async Task<bool> RunCycleAsync(
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
                    return true;

                if (!settings.Quiet)
                    AnsiConsole.MarkupLine($"[dim]* change detected ({Markup.Escape(reason)}); snapshotting...[/]");

                await CreateSnapshotAsync(repo, plan.Project, settings.Quiet, token);
                if (plan.DoSync)
                    return await SyncAndVerifyAsync(repo, plan, settings, token);
                return true;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                Log.Exception(ex, "Watcher snapshot database constraint failed");
                Console.Error.WriteLine("Watcher snapshot database write failed. See the VaultSync CLI log for details.");
                return false;
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
            var snapSvc = new SnapshotService(repo, new HashService(), CliVaultLogger.Instance);
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

        private static async Task<bool> SyncAndVerifyAsync(
            SqliteRepository repo,
            WatchPlan plan,
            WatchSettings settings,
            CancellationToken token)
        {
            string dest = ConfigHelper.ExpandUserPath(settings.Destination!);
            var syncSvc = new SyncService(CliVaultLogger.Instance);
            int code = await syncSvc.SyncAsync(plan.Project, dest, settings.DryRun, token);
            if (code != 0)
            {
                Console.Error.WriteLine($"Watcher mirror failed (exit {code}). See the VaultSync CLI log for details.");
                return false;
            }

            if (!settings.Quiet)
                AnsiConsole.MarkupLine("[green]Sync complete[/]");

            if (plan.DoVerify)
                return await VerifyAsync(repo, plan.Project, dest, settings.Quiet, token);
            return true;
        }

        private static async Task<bool> VerifyAsync(
            SqliteRepository repo,
            Core.Models.Project project,
            string dest,
            bool quiet,
            CancellationToken token)
        {
            var verifySvc = new VerifyService(repo, new HashService());
            VerifyResult result = await verifySvc.VerifyAsync(project, dest, percent: 100, full: true, token);
            if (result.Failures.Count > 0)
            {
                Console.Error.WriteLine($"Watcher verification failed: {result.Failures.Count} issue(s).");
                return false;
            }
            else if (!quiet)
                AnsiConsole.MarkupLine("[green]Verify OK[/]");
            return true;
        }
    }
}
