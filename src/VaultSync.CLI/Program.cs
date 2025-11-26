using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Utils; // for Log.Init()

// Keep the app focused on bootstrapping & wiring only.
// All commands, helpers, and utilities now live in separate files/namespaces.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Initialize logging (implemented in Utils/Log.cs)
        Log.Init();

        try
        {
            var app = new Spectre.Console.Cli.CommandApp();

            app.Configure(cfg =>
            {
                // Core commands
                cfg.AddCommand<VaultSync.CLI.Commands.PruneCommand>("prune")
                    .WithDescription("Delete old snapshots by count (--keep-last) or date (--before). Supports --dry-run.");

                cfg.AddCommand<VaultSync.CLI.Commands.SetPathCommand>("set-path")
                    .WithDescription("Update a project's root path");

                cfg.AddCommand<VaultSync.CLI.Commands.SetPathCommand>("update-path")
                    .WithDescription("Alias of set-path: update a project's root path");

                cfg.AddCommand<VaultSync.CLI.Commands.RestoreCommand>("restore")
                    .WithDescription("Restore a snapshot (latest by default) into a destination folder");

                cfg.AddCommand<VaultSync.CLI.Commands.HistoryCommand>("history")
                    .WithDescription("Show snapshot history for a project");

                cfg.AddCommand<VaultSync.CLI.Commands.DiffCommand>("diff")
                    .WithDescription("Compare two snapshots (default: latest vs previous)");

                cfg.AddCommand<VaultSync.CLI.Commands.SelfTestCommand>("self-test")
                    .WithDescription("Run an end-to-end smoke test (temp project -> snapshot -> sync -> verify)");

                cfg.AddCommand<VaultSync.CLI.Commands.InitCommand>("init")
                    .WithDescription("Initialize local config & database path");

                cfg.AddCommand<VaultSync.CLI.Commands.AddProjectCommand>("add-project")
                    .WithDescription("Register a folder with a preset");

                cfg.AddCommand<VaultSync.CLI.Commands.RemoveProjectCommand>("remove-project")
                    .WithDescription("Remove a project (and all snapshots/files)");

                cfg.AddCommand<VaultSync.CLI.Commands.ListProjectsCommand>("list-projects")
                    .WithDescription("List registered projects");

                cfg.AddCommand<VaultSync.CLI.Commands.SnapshotCommand>("snapshot")
                    .WithDescription("Create a snapshot (scan + hash) of a project");

                cfg.AddCommand<VaultSync.CLI.Commands.SyncCommand>("sync")
                    .WithDescription("Mirror a project folder to a destination (rsync/robocopy)");

                cfg.AddCommand<VaultSync.CLI.Commands.VerifyCommand>("verify")
                    .WithDescription("Verify destination matches latest snapshot (sample or full)");

                cfg.AddCommand<VaultSync.CLI.Commands.WatchCommand>("watch")
                    .WithDescription("Watch a project for file changes and auto-snapshot/sync/verify");

                cfg.AddCommand<VaultSync.CLI.Commands.DoctorCommand>("doctor")
                    .WithDescription("Check environment: rsync, DB path, project paths, and optional destination writability");

                cfg.AddCommand<VaultSync.CLI.Commands.VersionCommand>("version")
                    .WithDescription("Show version");

                // Branches
                cfg.AddBranch<CommandSettings>("config", b =>
                {
                    b.SetDescription("Inspect and modify VaultSync configuration");
                    b.AddCommand<VaultSync.CLI.Commands.ConfigShowCommand>("show").WithDescription("Print full JSON config");
                    b.AddCommand<VaultSync.CLI.Commands.ConfigPathCommand>("path").WithDescription("Print the configured database path");
                    b.AddCommand<VaultSync.CLI.Commands.ConfigSetDbCommand>("set-db").WithDescription("Set database path (e.g., set-db ~/.vaultsync/vault.db)");
                });

                cfg.AddBranch<CommandSettings>("presets", b =>
                {
                    b.SetDescription("View preset names and contents");
                    b.AddCommand<VaultSync.CLI.Commands.PresetsListCommand>("list").WithDescription("List available presets");
                    b.AddCommand<VaultSync.CLI.Commands.PresetsShowCommand>("show").WithDescription("Show the content of a preset");
                });
            });

            Log.Info($"argv: {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\\\"{a}\\\"" : a))}");
            var code = await app.RunAsync(args);
            Log.Info($"exit: {code}");
            return code;
        }
        catch (Exception ex)
        {
            Log.Exception(ex, "Main");
            // Preserve Spectre error output behavior.
            throw;
        }
    }
}
