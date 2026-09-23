using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Config;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.CLI.Commands;

internal sealed class VerifyAllBackupsSettings : CommandSettings
{
    [CommandOption("--project <NAME>")] public string? Project { get; init; }
    [CommandOption("--filter <TEXT>")] public string? Filter { get; init; }
    [CommandOption("--db <PATH>")] public string? Db { get; init; }
    [CommandOption("--limit <COUNT>")] public int Limit { get; init; } = 100;
    [CommandOption("--failure-limit <COUNT>")] public int FailureLimit { get; init; } = 20;
    [CommandOption("--output <FORMAT>")] public string? Output { get; init; }
}

internal sealed class VerifyAllBackupsCommand : AsyncCommand<VerifyAllBackupsSettings>
{
    private sealed record SelectedBackup(Project Project, Backup Backup);
    private sealed record CheckedBackup(string Project, BackupVerificationService.Outcome Outcome)
    {
        public object Details => new
        {
            project = Project,
            backupId = Outcome.BackupId,
            snapshotId = Outcome.SnapshotId,
            status = Outcome.ExitCode == 0 ? "passed" : "failed",
            code = Outcome.ErrorCode,
            checkedFiles = Outcome.CheckedFiles,
            passedFiles = Outcome.PassedFiles,
            failedFiles = Outcome.FailedFiles,
            failures = Outcome.Failures,
            failuresTruncated = Outcome.FailuresTruncated,
            payloadChecked = Outcome.PayloadChecked
        };
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, VerifyAllBackupsSettings settings,
        CancellationToken cancellationToken)
    {
        string? invalid = CommandOutput.Validate(settings.Output);
        if (settings.Limit <= 0 || settings.FailureLimit <= 0)
            invalid = "--limit and --failure-limit must be greater than zero.";
        if (settings.Project is not null && string.IsNullOrWhiteSpace(settings.Project))
            invalid = "--project must not be blank.";
        if (settings.Filter is not null && string.IsNullOrWhiteSpace(settings.Filter))
            invalid = "--filter must not be blank.";
        if (settings.Project is not null && settings.Filter is not null)
            invalid = "Choose either --project or --filter, not both.";
        if (invalid is not null)
            return CommandOutput.Failure(settings.Output, "backups.verify-all", "invalid_options", invalid, 2);

        try
        {
            return await VerifyAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CommandOutput.Failure(settings.Output, "backups.verify-all", "cancelled",
                "Bulk verification was cancelled before every selected backup completed.", 130);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            return CommandOutput.Failure(settings.Output, "backups.verify-all", "repository_unavailable",
                "Cannot inspect the selected repository. Check access and --db.", 1);
        }
    }

    private static async Task<int> VerifyAsync(VerifyAllBackupsSettings settings, CancellationToken cancellationToken)
    {
        var repository = new SqliteRepository(ConfigHelper.ResolveDb(settings.Db), readOnly: true);
        Project[] projects;
        if (settings.Project is not null)
        {
            Project? project = repository.GetProjectByName(settings.Project);
            if (project is null)
                return CommandOutput.Failure(settings.Output, "backups.verify-all", "project_not_found",
                    "No registered project matches --project.", 2);
            projects = [project];
        }
        else
        {
            projects = repository.GetAllProjects()
                .Where(project => settings.Filter is null ||
                    project.Name.Contains(settings.Filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        SelectedBackup[] selected = projects
            .SelectMany(project => repository.GetBackupsForProject(project.Id)
                .Select(backup => new SelectedBackup(project, backup))
                .OrderByDescending(item => item.Backup.CreatedUtc)
                .ThenByDescending(item => item.Backup.Id))
            .ToArray();
        var checkedBackups = new List<CheckedBackup>();
        AppConfig config = StaticAppConfigStore.Instance.Load();
        foreach (SelectedBackup item in selected.Take(settings.Limit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupVerificationService.Outcome outcome;
            try
            {
                outcome = await BackupVerificationService.CheckAsync(repository, item.Project,
                    item.Backup, config, settings.FailureLimit, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                outcome = BackupVerificationService.Error(item.Project, item.Backup, settings.FailureLimit,
                    "backup_unavailable", "The backup could not be inspected.", 1);
            }
            checkedBackups.Add(new CheckedBackup(item.Project.Name, outcome));
        }

        int passed = checkedBackups.Count(item => item.Outcome.ExitCode == 0);
        int failed = checkedBackups.Count - passed;
        bool truncated = selected.Length > checkedBackups.Count;
        var result = new
        {
            results = checkedBackups.Select(item => item.Details).ToArray(),
            count = checkedBackups.Count,
            totalCount = selected.Length,
            passedCount = passed,
            failedCount = failed,
            resultsTruncated = truncated
        };
        int exitCode = failed == 0 && !truncated ? 0 : passed > 0 || truncated ? 3 : 1;
        if (CommandOutput.IsJson(settings.Output))
            return exitCode == 0
                ? CommandOutput.Success("backups.verify-all", result)
                : CommandOutput.FailureWithDetails("backups.verify-all", "bulk_verification_incomplete",
                    "At least one selected backup failed or the result limit omitted backups.", result, exitCode);

        WriteTable(checkedBackups, selected.Length, passed, failed, truncated);
        return exitCode;
    }

    private static void WriteTable(IReadOnlyList<CheckedBackup> checkedBackups, int total,
        int passed, int failed, bool truncated)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Project");
        table.AddColumn("Backup ID");
        table.AddColumn("Result");
        table.AddColumn("Files");
        foreach (CheckedBackup item in checkedBackups)
            table.AddRow(Markup.Escape(item.Project), item.Outcome.BackupId.ToString(),
                item.Outcome.ErrorCode ?? "passed", item.Outcome.CheckedFiles.ToString());
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Checked {checkedBackups.Count}/{total} backup(s): " +
            $"{passed} passed, {failed} failed{(truncated ? "; more records omitted by --limit" : string.Empty)}.");
    }
}
