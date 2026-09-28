using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.Core.Repositories;
using VaultSync.CLI.Config;

namespace VaultSync.CLI.Commands
{
    sealed class DoctorSettings : CommandSettings
    {
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--check-dest <PATH>")] public string? CheckDest { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
        [CommandOption("--output <FORMAT>")] public string? Output { get; init; }
    }

    sealed class DoctorCommand : AsyncCommand<DoctorSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings s, CancellationToken cancellationToken)
        {
            string? invalid = CommandOutput.Validate(s.Output);
            if (invalid is not null)
                return CommandOutput.Failure(s.Output, "doctor", "invalid_options", invalid, 2);
            try { return await DiagnoseAsync(s, cancellationToken); }
            catch (OperationCanceledException) when (s.Output is not null)
            {
                return CommandOutput.Failure(s.Output, "doctor", "cancelled", "Diagnostics were cancelled before completion.", 130);
            }
        }

        private static async Task<int> DiagnoseAsync(DoctorSettings s, CancellationToken cancellationToken)
        {
            var reporter = new DoctorReporter(s.Quiet || CommandOutput.IsJson(s.Output));
            bool ok = await CheckSyncToolAsync(reporter, cancellationToken);
            ok &= await CheckDatabaseWritableAsync(s, reporter, cancellationToken);
            ok &= CheckProjects(s, reporter);

            if (!string.IsNullOrWhiteSpace(s.CheckDest))
                ok &= await CheckDestinationWritableAsync(s.CheckDest, reporter, cancellationToken);

            if (CommandOutput.IsJson(s.Output))
            {
                var data = new { checks = reporter.Checks, passedCount = reporter.PassedCount,
                    failedCount = reporter.FailedCount, warningCount = reporter.WarningCount };
                return ok ? CommandOutput.Success("doctor", data)
                    : CommandOutput.FailureWithDetails("doctor", "diagnostics_failed",
                        "One or more environment checks failed.", data, 2);
            }

            if (!s.Quiet)
                AnsiConsole.MarkupLine(ok ? "[green]Doctor: all good[/]" : "[red]Doctor: issues found[/]");

            return ok ? 0 : 2;
        }

        private static async Task<bool> CheckSyncToolAsync(DoctorReporter reporter, CancellationToken cancellationToken)
        {
            try
            {
                return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? await CheckRobocopyAsync(reporter, cancellationToken)
                    : await CheckRsyncAsync(reporter, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return reporter.Fail("sync_tool", "Platform sync tool not found on PATH");
            }
        }

        private static async Task<bool> CheckRobocopyAsync(DoctorReporter reporter, CancellationToken cancellationToken)
        {
            using System.Diagnostics.Process proc = StartProcess("robocopy", "/?");
            await ReadProcessOutputAsync(proc, cancellationToken);
            return proc.ExitCode <= 16
                ? reporter.Pass("sync_tool", "robocopy found (Windows sync runner)")
                : reporter.Fail("sync_tool", "robocopy returned unexpected exit");
        }

        private static async Task<bool> CheckRsyncAsync(DoctorReporter reporter, CancellationToken cancellationToken)
        {
            using System.Diagnostics.Process proc = StartProcess("rsync", "--version");
            string txt = await ReadProcessOutputAsync(proc, cancellationToken);
            return proc.ExitCode == 0 && txt.Contains("rsync", StringComparison.OrdinalIgnoreCase)
                ? reporter.Pass("sync_tool", "rsync found (Unix sync runner)")
                : reporter.Fail("sync_tool", "rsync not available or returned non-zero");
        }

        private static async Task<string> ReadProcessOutputAsync(
            System.Diagnostics.Process process,
            CancellationToken cancellationToken)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(output, process.StandardError.ReadToEndAsync(cancellationToken),
                process.WaitForExitAsync(cancellationToken));
            return await output;
        }

        private static System.Diagnostics.Process StartProcess(string fileName, string argument)
        {
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                ArgumentList = { argument },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            return System.Diagnostics.Process.Start(info)!;
        }

        private static async Task<bool> CheckDatabaseWritableAsync(
            DoctorSettings settings,
            DoctorReporter reporter,
            CancellationToken cancellationToken)
        {
            try
            {
                string db = ConfigHelper.ResolveDb(settings.Db);
                await WriteProbeAsync(Path.GetDirectoryName(db)!, cancellationToken);
                return reporter.Pass("database_writable", $"Database path writable: {db}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return reporter.Fail("database_writable", $"Database path not writable: {ex.Message}");
            }
        }

        private static bool CheckProjects(DoctorSettings settings, DoctorReporter reporter)
        {
            try
            {
                string db = ConfigHelper.ResolveDb(settings.Db);
                if (settings.Output is not null && !File.Exists(db))
                    return reporter.Fail("repository", "Database not found; initialize it explicitly before diagnostics");
                var repo = new SqliteRepository(db, readOnly: settings.Output is not null);
                if (settings.Output is null)
                    repo.EnsureSchema();
                return CheckProjectPaths(repo.ListProjects(), reporter);
            }
            catch (Exception ex)
            {
                return reporter.Fail("repository", $"Could not inspect projects: {ex.Message}");
            }
        }

        private static bool CheckProjectPaths(IEnumerable<Core.Models.Project> projects, DoctorReporter reporter)
        {
            var list = projects.ToList();
            if (list.Count == 0)
                reporter.Warn("projects", "No projects registered yet");

            bool ok = true;
            foreach (Core.Models.Project project in list)
            {
                ok &= Directory.Exists(project.RootPath)
                    ? reporter.Pass("project_path", $"Project path exists: {project.Name} -> {project.RootPath}")
                    : reporter.Fail("project_path", $"Project path missing: {project.Name} -> {project.RootPath}");
            }

            return ok;
        }

        private static async Task<bool> CheckDestinationWritableAsync(
            string rawDestination,
            DoctorReporter reporter,
            CancellationToken cancellationToken)
        {
            try
            {
                string dest = ConfigHelper.ExpandUserPath(rawDestination);
                await WriteProbeAsync(dest, cancellationToken);
                return reporter.Pass("destination_writable", $"Destination writable: {dest}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return reporter.Fail("destination_writable", $"Destination not writable: {ex.Message}");
            }
        }

        private static async Task WriteProbeAsync(string directory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directory);
            string testFile = Path.Combine(directory, $".vaultsync_write_test_{Guid.NewGuid():N}");
            await using var stream = new FileStream(testFile, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await stream.WriteAsync("ok"u8.ToArray(), cancellationToken);
        }

        private sealed class DoctorReporter(bool quiet)
        {
            public List<object> Checks { get; } = [];
            public int PassedCount { get; private set; }
            public int FailedCount { get; private set; }
            public int WarningCount { get; private set; }

            public bool Pass(string code, string msg)
            {
                Checks.Add(new { code, status = "pass" });
                PassedCount++;
                if (!quiet)
                    AnsiConsole.MarkupLine($"[green]+[/] {Markup.Escape(msg)}");

                return true;
            }

            public bool Fail(string code, string msg)
            {
                Checks.Add(new { code, status = "fail" });
                FailedCount++;
                if (!quiet)
                    AnsiConsole.MarkupLine($"[red]x[/] {Markup.Escape(msg)}");

                return false;
            }

            public void Warn(string code, string msg)
            {
                Checks.Add(new { code, status = "warning" });
                WarningCount++;
                if (!quiet)
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(msg)}[/]");
            }
        }
    }
}
