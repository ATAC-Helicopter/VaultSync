using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Config;

namespace VaultSync.CLI.Commands;

internal sealed class VerifyBackupSettings : CommandSettings
{
    [CommandArgument(0, "<project>")] public string Project { get; init; } = "";
    [CommandOption("--id <ID>")] public int? Id { get; init; }
    [CommandOption("--db <PATH>")] public string? Db { get; init; }
    [CommandOption("--limit <COUNT>")] public int Limit { get; init; } = 100;
    [CommandOption("--output <FORMAT>")] public string? Output { get; init; }
}

internal sealed class VerifyBackupCommand : AsyncCommand<VerifyBackupSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, VerifyBackupSettings settings, CancellationToken cancellationToken)
    {
        string? invalid = CommandOutput.Validate(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Project))
            invalid = "Project name must not be blank.";
        if (settings.Id is null)
            invalid = "Provide a backup selector with --id.";
        else if (settings.Id <= 0)
            invalid = "--id must be greater than zero.";
        if (settings.Limit <= 0)
            invalid = "--limit must be greater than zero.";
        if (invalid is not null)
            return CommandOutput.Failure(settings.Output, "backups.verify", "invalid_options", invalid, 2);

        try
        {
            return await VerifyAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CommandOutput.Failure(settings.Output, "backups.verify", "cancelled",
                "Backup verification was cancelled before it completed.", 130);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            return CommandOutput.Failure(settings.Output, "backups.verify", "repository_unavailable",
                "Cannot inspect the selected repository or backup. Check access, --db, and the recorded destination.", 1);
        }
    }

    private static async Task<int> VerifyAsync(VerifyBackupSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var repository = new SqliteRepository(ConfigHelper.ResolveDb(settings.Db), readOnly: true);
        Project? project = repository.GetProjectByName(settings.Project);
        if (project is null)
            return CommandOutput.Failure(settings.Output, "backups.verify", "project_not_found",
                "No registered project matches the supplied name.", 2);

        Backup? backup = repository.GetBackupsForProject(project.Id)
            .SingleOrDefault(candidate => candidate.Id == settings.Id);
        if (backup is null)
            return CommandOutput.Failure(settings.Output, "backups.verify", "backup_not_found",
                "No recorded backup with that ID belongs to the selected project.", 2);

        BackupVerificationService.Outcome result = await BackupVerificationService.CheckAsync(
            repository, project, backup, StaticAppConfigStore.Instance.Load(), settings.Limit,
            cancellationToken).ConfigureAwait(false);
        if (CommandOutput.IsJson(settings.Output))
            return result.ErrorCode is null
                ? CommandOutput.Success("backups.verify", result.Details)
                : result.ErrorCode == "verification_failed"
                    ? CommandOutput.FailureWithDetails("backups.verify", result.ErrorCode,
                        result.ErrorMessage!, result.Details, result.ExitCode)
                    : CommandOutput.Failure(settings.Output, "backups.verify", result.ErrorCode,
                        result.ErrorMessage!, result.ExitCode);

        if (result.ErrorCode is not null && result.ErrorCode != "verification_failed")
            return CommandOutput.Failure(settings.Output, "backups.verify", result.ErrorCode,
                result.ErrorMessage!, result.ExitCode);
        WriteResult(project, result);
        return result.ExitCode;
    }

    private static void WriteResult(Project project, BackupVerificationService.Outcome result)
    {
        AnsiConsole.MarkupLine($"Backup {result.BackupId} for [bold]{Markup.Escape(project.Name)}[/]: " +
            $"checked {result.CheckedFiles} file(s), failed {result.FailedFiles}.");
        if (result.FailedFiles == 0)
        {
            AnsiConsole.MarkupLine("[green]Recorded folder bytes match the indexed snapshot.[/]");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Path");
        table.AddColumn("Reason");
        foreach (BackupVerificationService.FileFailure failure in result.Failures)
            table.AddRow(Markup.Escape(failure.Path), failure.Reason);
        AnsiConsole.Write(table);
        if (result.FailuresTruncated)
            AnsiConsole.MarkupLine($"[yellow]{result.FailedFiles - result.Failures.Count} additional failure(s) omitted.[/]");
    }
}
