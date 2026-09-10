using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.CLI.Config;
using VaultSync.CLI.Services;

namespace VaultSync.CLI.Commands
{
    sealed class SyncSettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandArgument(1, "<destination>")] public string Destination { get; init; } = "";
        [CommandOption("--dry-run")] public bool DryRun { get; init; } = false;
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
    }

    sealed class SyncCommand : AsyncCommand<SyncSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, SyncSettings s, CancellationToken cancellationToken)
        {
            var db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            var proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");
            var dest = s.Destination.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            var svc = new SyncService();

            if (!s.Quiet)
            {
                if (s.DryRun)
                    AnsiConsole.MarkupLine($"[yellow]Dry run[/]: mirroring [blue]{Markup.Escape(proj.RootPath)}[/] -> [blue]{Markup.Escape(dest)}[/] (preset: {Markup.Escape(proj.Preset)})");
                else
                    AnsiConsole.MarkupLine($"Mirroring [blue]{Markup.Escape(proj.RootPath)}[/] -> [blue]{Markup.Escape(dest)}[/] (preset: {Markup.Escape(proj.Preset)})");
            }

            var started = DateTime.UtcNow;
            var code = await svc.SyncAsync(proj, dest, s.DryRun, cancellationToken);
            var took = DateTime.UtcNow - started;

            if (!s.Quiet)
            {
                if (code == 0) AnsiConsole.MarkupLine($"[green]Sync complete[/] in {took.TotalSeconds:F1}s (exit 0)");
                else AnsiConsole.MarkupLine($"[red]Sync failed[/] (exit {code})");
            }

            return code;
        }
    }

    sealed class VerifySettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandArgument(1, "<from>")] public string From { get; init; } = "";
        [CommandOption("--percent")] public int Percent { get; init; } = 10;
        [CommandOption("--full")] public bool Full { get; init; } = false;
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
        [CommandOption("--json")] public bool Json { get; init; } = false;
    }

    sealed class VerifyCommand : AsyncCommand<VerifySettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, VerifySettings s, CancellationToken cancellationToken)
        {
            var db = ConfigHelper.ResolveDb(s.Db);
            var repo = new SqliteRepository(db);
            repo.EnsureSchema();

            var proj = repo.GetProjectByName(s.Name) ?? throw new Exception($"Project '{s.Name}' not found");
            var src = s.From.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            var svc = new VerifyService(repo, new HashService());

            if (!s.Quiet && !s.Json)
                AnsiConsole.MarkupLine($"Verifying [blue]{Markup.Escape(proj.Name)}[/] against [blue]{Markup.Escape(src)}[/] - {(s.Full ? "full scan" : $"{s.Percent}% sample")}...");

            var started = DateTime.UtcNow;
            var result = await svc.VerifyAsync(proj, src, s.Percent, s.Full, cancellationToken);
            var took = DateTime.UtcNow - started;

            if (s.Json)
            {
                var payload = new
                {
                    project = proj.Name,
                    from = src,
                    full = s.Full,
                    percent = s.Percent,
                    checkedFiles = result.Checked,
                    failures = result.Failures.ConvertAll(f => new { f.Reason, f.RelPath, expected = f.Expected, actual = f.Actual }),
                    tookSeconds = Math.Round(took.TotalSeconds, 3),
                    exitCode = result.Failures.Count == 0 ? 0 : 2
                };
                Console.WriteLine(JsonSerializer.Serialize(payload, CommandJsonOptions.Indented));
                return result.Failures.Count == 0 ? 0 : 2;
            }

            if (result.Failures.Count == 0)
            {
                if (!s.Quiet)
                    AnsiConsole.MarkupLine($"[green]OK[/] - Checked {result.Checked} files, all good ({took.TotalSeconds:F1}s).");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Reason");
            table.AddColumn("Path");
            table.AddColumn("Expected");
            table.AddColumn("Actual");
            foreach (var f in result.Failures.Take(100))
                table.AddRow(f.Reason, f.RelPath, f.Expected ?? "-", f.Actual ?? "-");
            AnsiConsole.Write(table);

            if (!s.Quiet)
                AnsiConsole.MarkupLine($"[red]FAILED[/] - {result.Failures.Count} issues out of {result.Checked} checked ({took.TotalSeconds:F1}s).");

            return 2;
        }
    }

    sealed class RestoreSettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
        [CommandArgument(1, "<destination>")] public string Destination { get; init; } = "";
        [CommandOption("--snapshot")] public int? Snapshot { get; init; }
        [CommandOption("--dry-run")] public bool DryRun { get; init; } = false;
        [CommandOption("--clean")] public bool Clean { get; init; } = false;
        [CommandOption("--keep-empty-dirs")] public bool KeepEmptyDirs { get; init; } = false;
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
        [CommandOption("--json")] public bool Json { get; init; } = false;
    }

    sealed class RestoreCommand : AsyncCommand<RestoreSettings>
    {
        private sealed record RestoreSelection(
            Project Project,
            Snapshot Snapshot,
            Backup Backup,
            string SourceRoot,
            IReadOnlyList<FileEntry> Files);

        private sealed record RestoreCopy(string RelativePath, string SourcePath, string TargetPath);

        protected override Task<int> ExecuteAsync(CommandContext context, RestoreSettings s, CancellationToken cancellationToken)
        {
            var repo = new SqliteRepository(ConfigHelper.ResolveDb(s.Db));
            repo.EnsureSchema();
            RestoreSelection selection = ResolveSelection(repo, s);
            string destination = Path.GetFullPath(ExpandUserPath(s.Destination));
            EnsureDestinationIsSafe(selection, destination, s.Clean);
            IReadOnlyList<RestoreCopy> copies = BuildCopyPlan(selection, destination, cancellationToken);
            (IReadOnlyList<string> existingFiles, IReadOnlyList<string> existingDirectories) =
                InspectDestination(destination, s.Clean, cancellationToken);
            var started = DateTime.UtcNow;
            int copied = CopyBackupFiles(copies, destination, s, cancellationToken);
            int deleted = DeleteExtraFiles(existingFiles, copies, destination, s, cancellationToken);
            int deletedDirectories = DeleteEmptyDirectories(existingDirectories, destination, s);
            TimeSpan took = DateTime.UtcNow - started;

            WriteRestoreResult(s, selection, destination, copied, deleted, deletedDirectories, took);
            return Task.FromResult(0);
        }

        private static RestoreSelection ResolveSelection(SqliteRepository repo, RestoreSettings settings)
        {
            Project project = repo.GetProjectByName(settings.Name)
                ?? throw new InvalidOperationException($"Project '{settings.Name}' not found.");
            List<Backup> backups = [.. repo.GetBackupsForProject(project.Id)];
            Backup backup = settings.Snapshot is int requestedSnapshot
                ? backups.FirstOrDefault(item => item.SnapshotId == requestedSnapshot)
                    ?? throw new InvalidOperationException(
                        $"Snapshot {requestedSnapshot} has no recorded backup for project '{project.Name}'.")
                : backups.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Project '{project.Name}' has no recorded backups.");
            Snapshot snapshot = repo.GetSnapshotsForProject(project.Name)
                .FirstOrDefault(item => item.Id == backup.SnapshotId)
                ?? throw new InvalidDataException(
                    $"Backup {backup.Id} references missing snapshot {backup.SnapshotId}.");
            List<FileEntry> files = [.. repo.GetFilesForSnapshot(snapshot.Id)];
            if (files.Count == 0)
                throw new InvalidDataException($"Snapshot {snapshot.Id} has no files.");

            string sourceRoot = BackupContentPathResolver.Resolve(backup, ConfigHelper.Load())
                ?? throw new DirectoryNotFoundException(
                    $"The stored data for backup {backup.Id} is unavailable at its recorded destination.");
            if (backup.IsEncrypted ||
                File.Exists(Path.Combine(sourceRoot, BackupArchiveCryptoService.EncryptedArchiveFileName)) ||
                File.Exists(Path.Combine(sourceRoot, BackupArchiveCryptoService.PlainArchiveFileName)))
            {
                throw new NotSupportedException(
                    "CLI restore currently supports folder backups only. Use the desktop app to restore archive or encrypted backups.");
            }

            return new RestoreSelection(project, snapshot, backup, sourceRoot, files);
        }

        private static void EnsureDestinationIsSafe(
            RestoreSelection selection,
            string destination,
            bool clean)
        {
            if (BackupSafetyService.IsSameOrChildPath(selection.SourceRoot, destination))
                throw new InvalidOperationException("Refusing to restore into the selected backup data.");
            if (clean && BackupSafetyService.IsSameOrChildPath(destination, selection.SourceRoot))
                throw new InvalidOperationException("Refusing to --clean a destination containing the selected backup data.");
            if (clean && BackupSafetyService.IsSameOrChildPath(selection.Project.RootPath, destination))
                throw new InvalidOperationException("Refusing to --clean the project root or one of its children.");
        }

        private static IReadOnlyList<RestoreCopy> BuildCopyPlan(
            RestoreSelection selection,
            string destination,
            CancellationToken cancellationToken)
        {
            var copies = new List<RestoreCopy>(selection.Files.Count);
            var targets = new HashSet<string>(GetPathComparer());
            foreach (FileEntry file in selection.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!BackupSafetyService.TryResolveExistingFileUnderRoot(
                        selection.SourceRoot,
                        file.RelPath,
                        out string sourcePath))
                {
                    throw new InvalidDataException(
                        $"Backup {selection.Backup.Id} is missing or contains an unsafe file path: '{file.RelPath}'.");
                }
                if (!BackupSafetyService.TryResolvePathForWriteUnderRoot(
                        destination,
                        file.RelPath,
                        out string targetPath) ||
                    !targets.Add(targetPath))
                {
                    throw new InvalidDataException(
                        $"Snapshot {selection.Snapshot.Id} contains an unsafe or duplicate target path: '{file.RelPath}'.");
                }

                copies.Add(new RestoreCopy(file.RelPath.Replace('\\', '/'), sourcePath, targetPath));
            }

            return copies;
        }

        private static (IReadOnlyList<string> Files, IReadOnlyList<string> Directories) InspectDestination(
            string destination,
            bool clean,
            CancellationToken cancellationToken)
        {
            if (!clean || !Directory.Exists(destination))
                return ([], []);

            var files = new List<string>();
            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(destination);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string current = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException($"Refusing to --clean a destination containing a linked path: '{entry}'.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Add(entry);
                        pending.Push(entry);
                    }
                    else
                    {
                        files.Add(entry);
                    }
                }
            }

            return (files, directories);
        }

        private static int DeleteExtraFiles(
            IReadOnlyList<string> existingFiles,
            IReadOnlyList<RestoreCopy> copies,
            string destination,
            RestoreSettings settings,
            CancellationToken cancellationToken)
        {
            var retained = copies.Select(copy => copy.TargetPath).ToHashSet(GetPathComparer());
            int deleted = 0;
            foreach (string file in existingFiles.Where(file => !retained.Contains(file)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(destination, file).Replace('\\', '/');
                if (!settings.Quiet && !settings.Json)
                    AnsiConsole.MarkupLine($"[red]- delete[/] {Markup.Escape(relative)}");
                if (!settings.DryRun)
                    File.Delete(file);
                deleted++;
            }

            return deleted;
        }

        private static int CopyBackupFiles(
            IReadOnlyList<RestoreCopy> copies,
            string destination,
            RestoreSettings settings,
            CancellationToken cancellationToken)
        {
            if (!settings.DryRun)
                Directory.CreateDirectory(destination);
            foreach (RestoreCopy copy in copies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!settings.Quiet && !settings.Json)
                    AnsiConsole.MarkupLine($"[green]+ write[/] {Markup.Escape(copy.RelativePath)}");
                if (settings.DryRun)
                    continue;

                if (!BackupSafetyService.TryResolvePathForWriteUnderRoot(
                        destination,
                        copy.RelativePath,
                        out string checkedTarget) ||
                    !string.Equals(checkedTarget, copy.TargetPath, GetPathComparison()))
                {
                    throw new IOException($"Restore target became unsafe before writing '{copy.RelativePath}'.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(copy.TargetPath)!);
                CopyFileAtomically(copy.SourcePath, copy.TargetPath);
            }

            return copies.Count;
        }

        private static void CopyFileAtomically(string sourcePath, string targetPath)
        {
            string temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.vaultsync-restore.tmp";
            try
            {
                File.Copy(sourcePath, temporaryPath, overwrite: false);
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RuntimeLog.WriteVerbose(
                        $"[CLI Restore] Failed to remove temporary file '{temporaryPath}': {ex.Message}");
                }
            }
        }

        private static int DeleteEmptyDirectories(
            IReadOnlyList<string> directories,
            string destination,
            RestoreSettings settings)
        {
            if (!settings.Clean || settings.KeepEmptyDirs)
                return 0;

            int deleted = 0;
            foreach (string directory in directories.OrderByDescending(path => path.Length))
            {
                if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                    continue;
                if (!settings.DryRun)
                    Directory.Delete(directory);
                deleted++;
                if (!settings.Quiet && !settings.Json)
                {
                    string relative = Path.GetRelativePath(destination, directory).Replace('\\', '/');
                    AnsiConsole.MarkupLine($"[red]- rmdir[/] {Markup.Escape(relative)}");
                }
            }

            return deleted;
        }

        private static void WriteRestoreResult(
            RestoreSettings settings,
            RestoreSelection selection,
            string destination,
            int copied,
            int deleted,
            int deletedDirectories,
            TimeSpan took)
        {
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    project = selection.Project.Name,
                    snapshotId = selection.Snapshot.Id,
                    snapshotCreatedUtc = selection.Snapshot.CreatedUtc.ToString("u"),
                    backupId = selection.Backup.Id,
                    destination,
                    dryRun = settings.DryRun,
                    clean = settings.Clean,
                    keepEmptyDirs = settings.KeepEmptyDirs,
                    deleted,
                    deletedDirs = deletedDirectories,
                    copied,
                    missingFromSource = 0,
                    tookSeconds = Math.Round(took.TotalSeconds, 3),
                    exitCode = 0
                }, CommandJsonOptions.Indented));
                return;
            }
            if (settings.Quiet)
                return;

            string heading = settings.DryRun ? "[yellow]Dry restore[/]" : "Restore";
            AnsiConsole.MarkupLine(
                $"{heading}: [bold]{Markup.Escape(selection.Project.Name)}[/] backup [bold]{selection.Backup.Id}[/], " +
                $"snapshot [bold]{selection.Snapshot.Id}[/] ([grey]{selection.Snapshot.CreatedUtc:u}[/]) -> " +
                $"[blue]{Markup.Escape(destination)}[/]");
            if (settings.Clean && settings.DryRun)
                AnsiConsole.MarkupLine("[grey]Note[/]: --clean would remove the extra files shown above.");
            string mode = settings.DryRun ? "[yellow]Dry restore complete[/]" : "[green]Restore complete[/]";
            AnsiConsole.MarkupLine(
                $"{mode} - copied: {copied}, deleted: {deleted}, deleted-dirs: {deletedDirectories} ({took.TotalSeconds:F1}s).");
        }

        private static string ExpandUserPath(string path)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path == "~")
                return home;
            return path.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                ? Path.Combine(home, path[2..])
                : path;
        }

        private static StringComparer GetPathComparer() =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        private static StringComparison GetPathComparison() =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
    }

    sealed class SelfTestSettings : CommandSettings
    {
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
    }

    sealed class SelfTestCommand : AsyncCommand<SelfTestSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, SelfTestSettings s, CancellationToken cancellationToken)
        {
            string? explicitDatabasePath = string.IsNullOrWhiteSpace(s.Db)
                ? null
                : ConfigHelper.ResolveDb(s.Db);
            SelfTestRunResult result = await new SelfTestRunner().RunAsync(
                explicitDatabasePath,
                cancellationToken);

            if (!s.Quiet)
                WriteResult(result);

            return result.ExitCode;
        }

        internal static void WriteResult(SelfTestRunResult result)
        {
            string databaseKind = result.UsesTemporaryDatabase
                ? "isolated temporary database"
                : "explicit database";
            AnsiConsole.MarkupLine(
                $"[blue]Self-test[/] using {databaseKind}: {Markup.Escape(result.DatabasePath)}");
            AnsiConsole.MarkupLine(
                $"Registered project [bold]{Markup.Escape(result.ProjectName)}[/] " +
                $"(id {result.ProjectId})");
            AnsiConsole.MarkupLine($"Snapshot {result.SnapshotId} created");

            if (result.SyncExitCode != 0)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Sync failed[/] (exit {result.SyncExitCode})");
                return;
            }

            AnsiConsole.MarkupLine("[green]Sync OK[/]");
            if (result.VerificationFailures > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Verify failed[/]: {result.VerificationFailures} issues");
                return;
            }

            AnsiConsole.MarkupLine("[green]Verify OK[/] - all files matched");
            if (!result.UsesTemporaryDatabase)
                AnsiConsole.MarkupLine(
                    $"[grey]Cleanup[/]: removed temporary project metadata; " +
                    $"files remain under {Markup.Escape(result.WorkspacePath)}");
        }
    }
}
