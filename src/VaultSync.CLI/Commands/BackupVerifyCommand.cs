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
using VaultSync.Core.Services;

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
    private sealed record Failure(string Path, string Reason);

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

        if (!repository.GetSnapshotsForProject(project.Name).Any(snapshot => snapshot.Id == backup.SnapshotId))
            return CommandOutput.Failure(settings.Output, "backups.verify", "snapshot_not_found",
                "The backup does not reference a snapshot owned by the selected project.", 2);

        if (backup.IsEncrypted || !string.Equals(backup.BackupMode, BackupModes.Full, StringComparison.OrdinalIgnoreCase))
            return CommandOutput.Failure(settings.Output, "backups.verify", "unsupported_backup_format",
                "This CLI check supports full, unencrypted folder backups only.", 2);

        string? sourceRoot = BackupContentPathResolver.Resolve(backup, StaticAppConfigStore.Instance.Load());
        if (sourceRoot is null || !Directory.Exists(sourceRoot))
            return CommandOutput.Failure(settings.Output, "backups.verify", "backup_unavailable",
                "The recorded backup folder is unavailable at its selected destination.", 1);
        if (File.Exists(Path.Combine(sourceRoot, BackupArchiveCryptoService.PlainArchiveFileName)) ||
            File.Exists(Path.Combine(sourceRoot, BackupArchiveCryptoService.EncryptedArchiveFileName)))
            return CommandOutput.Failure(settings.Output, "backups.verify", "unsupported_backup_format",
                "This CLI check supports recorded folder backups only; archive data requires a qualified verifier.", 2);

        FileEntry[] files = repository.GetFilesForSnapshot(backup.SnapshotId).ToArray();
        if (files.Length == 0)
            return CommandOutput.Failure(settings.Output, "backups.verify", "snapshot_history_empty",
                "The backup's snapshot has no indexed files to verify.", 2);

        var hash = new HashService();
        var failures = new List<Failure>();
        int failedCount = 0;
        foreach (FileEntry file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Failure? failure = await CheckFileAsync(sourceRoot, file, hash, cancellationToken).ConfigureAwait(false);
            if (failure is null)
                continue;
            failedCount++;
            if (failures.Count < settings.Limit)
                failures.Add(failure);
        }

        var result = new
        {
            projectId = project.Id,
            backupId = backup.Id,
            snapshotId = backup.SnapshotId,
            checkedFiles = files.Length,
            passedFiles = files.Length - failedCount,
            failedFiles = failedCount,
            failures,
            failureLimit = settings.Limit,
            failuresTruncated = failedCount > failures.Count,
            payloadChecked = true
        };
        if (CommandOutput.IsJson(settings.Output))
            return failedCount == 0
                ? CommandOutput.Success("backups.verify", result)
                : CommandOutput.FailureWithDetails("backups.verify", "verification_failed",
                    "Recorded backup bytes did not match the selected snapshot.", result, 1);

        WriteResult(project, backup, files.Length, failedCount, failures);
        return failedCount == 0 ? 0 : 1;
    }

    private static async Task<Failure?> CheckFileAsync(string sourceRoot, FileEntry file, HashService hash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file.HashSha256))
            return new Failure(file.RelPath, "hash_unavailable");
        if (!BackupSafetyService.TryResolveExistingFileUnderRoot(sourceRoot, file.RelPath, out string sourcePath))
            return new Failure(file.RelPath, "missing_or_unsafe");
        try
        {
            string actual = await hash.Sha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
            return string.Equals(actual, file.HashSha256, StringComparison.OrdinalIgnoreCase)
                ? null : new Failure(file.RelPath, "hash_mismatch");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new Failure(file.RelPath, "read_error");
        }
    }

    private static void WriteResult(Project project, Backup backup, int checkedCount, int failedCount,
        IReadOnlyList<Failure> failures)
    {
        AnsiConsole.MarkupLine($"Backup {backup.Id} for [bold]{Markup.Escape(project.Name)}[/]: " +
            $"checked {checkedCount} file(s), failed {failedCount}.");
        if (failedCount == 0)
        {
            AnsiConsole.MarkupLine("[green]Recorded folder bytes match the indexed snapshot.[/]");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Path");
        table.AddColumn("Reason");
        foreach (Failure failure in failures)
            table.AddRow(Markup.Escape(failure.Path), failure.Reason);
        AnsiConsole.Write(table);
        if (failedCount > failures.Count)
            AnsiConsole.MarkupLine($"[yellow]{failedCount - failures.Count} additional failure(s) omitted.[/]");
    }
}
