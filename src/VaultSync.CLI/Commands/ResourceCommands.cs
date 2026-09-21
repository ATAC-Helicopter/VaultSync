using Spectre.Console.Cli;

namespace VaultSync.CLI.Commands;

/// <summary>
/// Registers resource/action routes over the existing command implementations.
/// Legacy routes remain registered by Program during the 1.9 compatibility window.
/// </summary>
internal static class ResourceCommands
{
    public static void Register(IConfigurator configuration)
    {
        configuration.AddBranch<CommandSettings>("projects", projects =>
        {
            projects.SetDescription("Register, inspect, discover, and manage protected project sources");
            projects.AddCommand<AddProjectCommand>("add")
                .WithDescription("Register an existing source folder with a preset");
            projects.AddCommand<ListProjectsCommand>("list")
                .WithDescription("List projects with filters/limits; --output json provides versioned results");
            projects.AddCommand<ShowProjectCommand>("show")
                .WithDescription("Inspect one project by literal name or explicit --id; supports --output json");
            projects.AddCommand<DiscoverProjectsCommand>("discover")
                .WithDescription("Discover candidate source folders beneath Projects Root; use --root to select a root");
            projects.AddCommand<SetPathCommand>("set-path")
                .WithDescription("Update a registered project's source folder");
            projects.AddCommand<RemoveProjectCommand>("remove")
                .WithDescription("Unregister a project and its local history index; source and backup files remain");
        });

        configuration.AddBranch<CommandSettings>("snapshots", snapshots =>
        {
            snapshots.SetDescription("Index source state and inspect local snapshot history; snapshots alone are not stored backups");
            snapshots.AddCommand<SnapshotCommand>("create")
                .WithDescription("Scan and hash a project into the local index; does not store backup bytes");
            snapshots.AddCommand<HistoryCommand>("list")
                .WithDescription("List a project's indexed snapshots; --output json provides versioned read-only results");
            snapshots.AddCommand<ShowSnapshotCommand>("show")
                .WithDescription("Inspect one same-project snapshot, history markers, and recorded backup references");
            snapshots.AddCommand<DiffCommand>("diff")
                .WithDescription("Compare two same-project snapshots; --output json provides bounded versioned results");
            snapshots.AddCommand<PruneCommand>("prune")
                .WithDescription("Prune eligible local snapshot indexes; protected and backed snapshots remain; supports --dry-run");
        });

        configuration.AddBranch<CommandSettings>("recovery", recovery =>
        {
            recovery.SetDescription("Recover recorded backup data through supported restore workflows");
            recovery.AddCommand<RestoreCommand>("restore")
                .WithDescription("Restore a recorded folder backup; archive/encrypted recovery is not supported yet");
        });

        configuration.AddCommand<SyncCommand>("mirror")
            .WithDescription("Mirror live project files with rsync/robocopy; does not create a recorded backup; supports --dry-run");
    }
}
