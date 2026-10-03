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

internal sealed class ListBackupsSettings : CommandSettings
{
    [CommandArgument(0, "<project>")] public string Project { get; init; } = "";
    [CommandOption("--db <PATH>")] public string? Db { get; init; }
    [CommandOption("--limit <COUNT>")] public int? Limit { get; init; }
    [CommandOption("--output <FORMAT>")] public string? Output { get; init; }
}

internal sealed class ListBackupsCommand : AsyncCommand<ListBackupsSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, ListBackupsSettings settings, CancellationToken cancellationToken)
    {
        string? invalid = CommandOutput.Validate(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Project))
            invalid = "Project name must not be blank.";
        if (settings.Limit is <= 0)
            invalid = "--limit must be greater than zero.";
        if (invalid is not null)
            return Task.FromResult(CommandOutput.Failure(settings.Output, "backups.list", "invalid_options", invalid, 2));

        return Task.FromResult(CommandInspection.Run(settings.Output, "backups.list", () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repository = new SqliteRepository(ConfigHelper.ResolveDb(settings.Db), readOnly: true);
            Project? project = repository.GetProjectByName(settings.Project);
            if (project is null)
                return CommandOutput.Failure(settings.Output, "backups.list", "project_not_found",
                    "No registered project matches the supplied name.", 2);

            Backup[] all = repository.GetBackupsForProject(project.Id)
                .OrderByDescending(backup => backup.CreatedUtc)
                .ThenByDescending(backup => backup.Id)
                .ToArray();
            Backup[] rows = settings.Limit is int limit ? all.Take(limit).ToArray() : all;
            if (CommandOutput.IsJson(settings.Output))
                return CommandOutput.Success("backups.list", new
                {
                    project = ProjectInspection.Describe(project),
                    backups = rows.Select(BackupInspection.Describe).ToArray(),
                    count = rows.Length,
                    totalCount = all.Length
                });

            BackupInspection.WriteTable(project, rows);
            return 0;
        }));
    }
}

internal sealed class ShowBackupSettings : CommandSettings
{
    [CommandArgument(0, "<project>")] public string Project { get; init; } = "";
    [CommandOption("--id <ID>")] public int? Id { get; init; }
    [CommandOption("--db <PATH>")] public string? Db { get; init; }
    [CommandOption("--output <FORMAT>")] public string? Output { get; init; }
}

internal sealed class ShowBackupCommand : AsyncCommand<ShowBackupSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, ShowBackupSettings settings, CancellationToken cancellationToken)
    {
        string? invalid = CommandOutput.Validate(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Project))
            invalid = "Project name must not be blank.";
        if (settings.Id is null)
            invalid = "Provide a backup selector with --id.";
        else if (settings.Id <= 0)
            invalid = "--id must be greater than zero.";
        if (invalid is not null)
            return Task.FromResult(CommandOutput.Failure(settings.Output, "backups.show", "invalid_options", invalid, 2));

        return Task.FromResult(CommandInspection.Run(settings.Output, "backups.show", () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repository = new SqliteRepository(ConfigHelper.ResolveDb(settings.Db), readOnly: true);
            Project? project = repository.GetProjectByName(settings.Project);
            if (project is null)
                return CommandOutput.Failure(settings.Output, "backups.show", "project_not_found",
                    "No registered project matches the supplied name.", 2);

            Backup? backup = repository.GetBackupsForProject(project.Id)
                .SingleOrDefault(candidate => candidate.Id == settings.Id);
            if (backup is null)
                return CommandOutput.Failure(settings.Output, "backups.show", "backup_not_found",
                    "No recorded backup with that ID belongs to the selected project.", 2);

            BackupInspection.BackupDetails details = BackupInspection.Describe(backup);
            if (CommandOutput.IsJson(settings.Output))
                return CommandOutput.Success("backups.show", new
                {
                    project = ProjectInspection.Describe(project),
                    backup = details
                });

            BackupInspection.WriteDetails(project, details);
            return 0;
        }));
    }
}

internal static class BackupInspection
{
    internal sealed record BackupDetails(int Id, string ExternalId, int SnapshotId, string CreatedUtc,
        string Type, string BackupMode, long TotalBytes, bool IsProtected, bool IsEncrypted,
        bool IsImported, bool PayloadChecked);

    public static BackupDetails Describe(Backup backup) => new(
        backup.Id,
        backup.ExternalId,
        backup.SnapshotId,
        DateTime.SpecifyKind(backup.CreatedUtc, DateTimeKind.Utc).ToString("O"),
        backup.Type,
        backup.BackupMode,
        backup.TotalBytes,
        backup.IsProtected,
        backup.IsEncrypted,
        backup.IsImported,
        PayloadChecked: false);

    public static void WriteTable(Project project, Backup[] backups)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Backup ID");
        table.AddColumn("Snapshot ID");
        table.AddColumn("Created (UTC)");
        table.AddColumn("Mode");
        table.AddColumn("Bytes");
        table.AddColumn("Protected");
        foreach (Backup backup in backups)
            table.AddRow(backup.Id.ToString(), backup.SnapshotId.ToString(), backup.CreatedUtc.ToString("u"),
                Markup.Escape(backup.BackupMode), backup.TotalBytes.ToString(), backup.IsProtected ? "yes" : "no");
        AnsiConsole.MarkupLine($"Recorded backups for [bold]{Markup.Escape(project.Name)}[/]: {backups.Length}");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Records only; payload availability and integrity were not checked.[/]");
    }

    public static void WriteDetails(Project project, BackupDetails details)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddRow("Project", Markup.Escape(project.Name));
        table.AddRow("Backup ID", details.Id.ToString());
        table.AddRow("Snapshot ID", details.SnapshotId.ToString());
        table.AddRow("Created (UTC)", details.CreatedUtc);
        table.AddRow("Type", Markup.Escape(details.Type));
        table.AddRow("Mode", Markup.Escape(details.BackupMode));
        table.AddRow("Bytes", details.TotalBytes.ToString());
        table.AddRow("Protected", details.IsProtected ? "yes" : "no");
        table.AddRow("Encrypted", details.IsEncrypted ? "yes" : "no");
        table.AddRow("Imported", details.IsImported ? "yes" : "no");
        table.AddRow("Payload checked", "no");
        AnsiConsole.Write(table);
    }
}
