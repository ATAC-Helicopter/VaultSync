using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.CLI.Commands;

internal sealed class ShowSnapshotSettings : CommandSettings
{
    [CommandArgument(0, "<project>")] public string Project { get; init; } = "";
    [CommandOption("--id <ID>")] public int? Id { get; init; }
    [CommandOption("--db <PATH>")] public string? Db { get; init; }
    [CommandOption("--output <FORMAT>")] public string? Output { get; init; }
}

internal sealed class ShowSnapshotCommand : AsyncCommand<ShowSnapshotSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, ShowSnapshotSettings settings, CancellationToken cancellationToken)
    {
        string? invalid = CommandOutput.Validate(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Project))
            invalid = "Project name must not be blank.";
        if (settings.Id is null)
            invalid = "Provide a snapshot selector with --id.";
        else if (settings.Id <= 0)
            invalid = "--id must be greater than zero.";
        if (invalid is not null)
            return Task.FromResult(CommandOutput.Failure(settings.Output, "snapshots.show", "invalid_options", invalid, 2));

        return Task.FromResult(CommandInspection.Run(settings.Output, "snapshots.show", () =>
            Inspect(settings, cancellationToken)));
    }

    private static int Inspect(ShowSnapshotSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var repository = new SqliteRepository(ConfigHelper.ResolveDb(settings.Db), readOnly: true);
        Project? project = repository.GetProjectByName(settings.Project);
        if (project is null)
            return CommandOutput.Failure(settings.Output, "snapshots.show", "project_not_found",
                "No registered project matches the supplied name.", 2);

        Snapshot? snapshot = repository.GetSnapshotsForProject(project.Name)
            .SingleOrDefault(candidate => candidate.Id == settings.Id);
        if (snapshot is null)
            return CommandOutput.Failure(settings.Output, "snapshots.show", "snapshot_not_found",
                "No snapshot with that ID belongs to the selected project.", 2);

        SnapshotHistoryMetadata? history = repository.GetSnapshotHistoryMetadata(snapshot.Id);
        Backup[] backupRecords = repository.GetBackupsForProject(project.Id)
            .Where(backup => backup.SnapshotId == snapshot.Id)
            .ToArray();
        SnapshotDetails details = Describe(snapshot, history, backupRecords);
        if (CommandOutput.IsJson(settings.Output))
            return CommandOutput.Success("snapshots.show", new
            {
                project = ProjectInspection.Describe(project),
                snapshot = details
            });

        WriteTable(project, details);
        return 0;
    }

    private static SnapshotDetails Describe(Snapshot snapshot, SnapshotHistoryMetadata? history, Backup[] backups) => new(
        snapshot.Id,
        snapshot.ExternalId,
        DateTime.SpecifyKind(snapshot.CreatedUtc, DateTimeKind.Utc).ToString("O"),
        snapshot.FileCount,
        snapshot.TotalBytes,
        new SnapshotChangeDetails(snapshot.DiffAdded, snapshot.DiffModified, snapshot.DiffDeleted, snapshot.DiffNetBytes),
        history is null ? null : new SnapshotHistoryDetails(history.Label, history.Note, history.Tags,
            history.IsProtected, history.IsKnownGood,
            DateTime.SpecifyKind(history.UpdatedUtc, DateTimeKind.Utc).ToString("O")),
        backups.Length,
        backups.Count(backup => backup.IsEncrypted),
        backups.Count(backup => backup.IsProtected));

    private static void WriteTable(Project project, SnapshotDetails details)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddRow("Project", Markup.Escape(project.Name));
        table.AddRow("Snapshot ID", details.Id.ToString());
        table.AddRow("Created (UTC)", details.CreatedUtc);
        table.AddRow("Files", details.FileCount.ToString());
        table.AddRow("Bytes", details.TotalBytes.ToString());
        table.AddRow("Changes", $"+{details.Changes.Added} ~{details.Changes.Modified} -{details.Changes.Deleted}");
        table.AddRow("Recorded backup records", details.RecordedBackupCount.ToString());
        table.AddRow("Encrypted backup records", details.EncryptedBackupCount.ToString());
        table.AddRow("Protected backup records", details.ProtectedBackupCount.ToString());
        if (details.History is not null)
        {
            table.AddRow("Label", Markup.Escape(details.History.Label));
            table.AddRow("Tags", Markup.Escape(details.History.Tags));
            table.AddRow("Protected", details.History.IsProtected ? "yes" : "no");
            table.AddRow("Known good", details.History.IsKnownGood ? "yes" : "no");
            if (!string.IsNullOrWhiteSpace(details.History.Note))
                table.AddRow("Note", Markup.Escape(details.History.Note));
        }
        AnsiConsole.Write(table);
    }

    private sealed record SnapshotDetails(int Id, string ExternalId, string CreatedUtc, long FileCount, long TotalBytes,
        SnapshotChangeDetails Changes, SnapshotHistoryDetails? History, int RecordedBackupCount,
        int EncryptedBackupCount, int ProtectedBackupCount);
    private sealed record SnapshotChangeDetails(int Added, int Modified, int Deleted, long NetBytes);
    private sealed record SnapshotHistoryDetails(string Label, string Note, string Tags, bool IsProtected,
        bool IsKnownGood, string UpdatedUtc);
}
