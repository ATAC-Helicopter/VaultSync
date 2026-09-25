using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Config;
using VaultSync.CLI.Utils;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using Microsoft.Data.Sqlite;

namespace VaultSync.CLI.Commands;

internal sealed class CreateBackupSettings : CommandSettings
{
    [CommandArgument(0, "<project>")] public string Project { get; init; } = "";
    [CommandOption("--destination <PATH>")] public string? Destination { get; init; }
    [CommandOption("--db <PATH>")] public string? Db { get; init; }
    [CommandOption("--dry-run")] public bool DryRun { get; init; }
    [CommandOption("--quiet")] public bool Quiet { get; init; }
    [CommandOption("--output <FORMAT>")] public string? Output { get; init; }
}

internal sealed class CreateBackupCommand : AsyncCommand<CreateBackupSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CreateBackupSettings settings,
        CancellationToken cancellationToken)
    {
        string? invalid = CommandOutput.Validate(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Project) || string.IsNullOrWhiteSpace(settings.Destination))
            invalid = "Provide a project and --destination PATH.";
        if (invalid is not null)
            return Fail(settings.Output, "invalid_options", invalid, 2);

        using IDisposable logScope = RuntimeLog.UseLogger(CliVaultLogger.Instance);
        try
        {
            string destination = Path.GetFullPath(ConfigHelper.ExpandUserPath(settings.Destination!));
            if (!Directory.Exists(destination))
                return Fail(settings.Output, "destination_unavailable", "The backup destination must be an existing, accessible directory.", 2);

            string database = ConfigHelper.ResolveDb(settings.Db);
            if (!File.Exists(database))
                return Fail(settings.Output, "repository_unavailable", "The selected database does not exist. Register the project first.", 1);

            var repository = new SqliteRepository(database, readOnly: settings.DryRun);
            Project? project = repository.GetProjectByName(settings.Project);
            if (project is null)
                return Fail(settings.Output, "project_not_found", "No registered project matches the supplied name.", 2);
            if (!Directory.Exists(project.RootPath))
                return Fail(settings.Output, "source_unavailable", "The registered project source is unavailable.", 1);

            try
            {
                BackupSafetyService.EnsureSafeBackupRoot(project, destination);
            }
            catch (InvalidOperationException)
            {
                return Fail(settings.Output, "unsafe_destination",
                    "The destination must be separate from the registered project source.", 2);
            }
            AppConfig config = StaticAppConfigStore.Instance.Load();
            if (BackupEncryptionPolicyResolver.Resolve(project,
                    config.Backups.Encryption ?? new BackupEncryptionConfig()).EncryptionRequested)
                return Fail(settings.Output, "unsupported_backup_format", "This CLI workflow supports full, unencrypted folder backups only; the project's encryption policy requires an archive.", 2);

            var service = new BackupService(repository, logger: CliVaultLogger.Instance);
            BackupService.BackupPreflightResult preflight = await service.PreflightBackupAsync(
                project, destination, ct: cancellationToken).ConfigureAwait(false);
            if (!preflight.HasEnoughSpace)
                return Fail(settings.Output, "insufficient_space", "The selected destination does not have enough free space for this backup.", 1);
            if (settings.DryRun)
            {
                if (CommandOutput.IsJson(settings.Output))
                    return CommandOutput.Success("backups.create", new
                    {
                        projectId = project.Id,
                        dryRun = true,
                        estimatedFiles = preflight.TotalFiles,
                        estimatedBytes = preflight.TotalBytes,
                        backupId = (int?)null,
                        snapshotId = (int?)null,
                        payloadChecked = false
                    });
                if (!settings.Quiet)
                    AnsiConsole.MarkupLine($"Backup plan for [bold]{Markup.Escape(project.Name)}[/]: " +
                        $"{preflight.TotalFiles} file(s), {preflight.TotalBytes} source byte(s), " +
                        $"destination {Markup.Escape(destination)}. No files or records changed.");
                return 0;
            }

            // Hash the selected source state before copying so this new backup can
            // subsequently be checked against its own immutable snapshot index.
            var snapshotService = new SnapshotService(repository, new HashService(), CliVaultLogger.Instance);
            int snapshotId = await snapshotService.CreateSnapshotAsync(project, fullHash: true,
                maxSnapshotsToKeep: null, ct: cancellationToken).ConfigureAwait(false);
            BackupService.BackupRunResult result = await service.RunBackupAsync(new BackupService.BackupRunRequest
            {
                Project = project,
                BackupRoot = destination,
                IsAuto = false,
                ReuseSnapshotId = snapshotId,
                DestinationPath = destination,
                WriteMetadata = true,
                UseArchiveMode = false,
                UseIncrementalBackups = false,
                MaxSnapshotsToKeep = null,
                CancellationToken = cancellationToken
            }).ConfigureAwait(false);
            if (result.Cancelled)
                return Fail(settings.Output, "cancelled", "Backup creation was cancelled before completion.", 130);
            if (result.BackupId <= 0)
                return Fail(settings.Output, "backup_not_created", "No backup record was created; inspect the private CLI log.", 1);

            if (CommandOutput.IsJson(settings.Output))
                return CommandOutput.Success("backups.create", new
                {
                    projectId = project.Id,
                    dryRun = false,
                    estimatedFiles = preflight.TotalFiles,
                    estimatedBytes = preflight.TotalBytes,
                    backupId = (int?)result.BackupId,
                    snapshotId = (int?)snapshotId,
                    payloadChecked = false
                });

            if (!settings.Quiet)
                AnsiConsole.MarkupLine($"[green]Recorded backup {result.BackupId} created[/] for " +
                    $"[bold]{Markup.Escape(project.Name)}[/] (snapshot {snapshotId}). " +
                    $"Run backups verify {Markup.Escape(project.Name)} --id {result.BackupId} to check stored bytes.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return Fail(settings.Output, "cancelled", "Backup creation was cancelled before completion.", 130);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail(settings.Output, "invalid_options", "The selected path is invalid.", 2);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            CliVaultLogger.Instance.Error($"Backup creation failed: {error}");
            return Fail(settings.Output, "backup_failed", "Backup creation failed. Check the selected source/destination and private CLI log.", 1);
        }
    }

    private static int Fail(string? output, string code, string message, int exitCode) =>
        CommandOutput.Failure(output, "backups.create", code, message, exitCode);
}
