using System;
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
    }

    sealed class DoctorCommand : AsyncCommand<DoctorSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings s, CancellationToken cancellationToken)
        {
            var reporter = new DoctorReporter(s.Quiet);
            bool ok = await CheckSyncToolAsync(reporter, cancellationToken);
            ok &= await CheckDatabaseWritableAsync(s, reporter, cancellationToken);
            ok &= CheckProjects(s, reporter);

            if (!string.IsNullOrWhiteSpace(s.CheckDest))
                ok &= await CheckDestinationWritableAsync(s.CheckDest, reporter, cancellationToken);

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
            catch
            {
                return reporter.Fail("Platform sync tool not found on PATH");
            }
        }

        private static async Task<bool> CheckRobocopyAsync(DoctorReporter reporter, CancellationToken cancellationToken)
        {
            using System.Diagnostics.Process proc = StartProcess("robocopy", "/?");
            await proc.WaitForExitAsync(cancellationToken);
            return proc.ExitCode <= 16
                ? reporter.Pass("robocopy found (Windows sync runner)")
                : reporter.Fail("robocopy returned unexpected exit");
        }

        private static async Task<bool> CheckRsyncAsync(DoctorReporter reporter, CancellationToken cancellationToken)
        {
            using System.Diagnostics.Process proc = StartProcess("rsync", "--version");
            string txt = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);
            return proc.ExitCode == 0 && txt.Contains("rsync", StringComparison.OrdinalIgnoreCase)
                ? reporter.Pass("rsync found (Unix sync runner)")
                : reporter.Fail("rsync not available or returned non-zero");
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
                return reporter.Pass($"Database path writable: {db}");
            }
            catch (Exception ex)
            {
                return reporter.Fail($"Database path not writable: {ex.Message}");
            }
        }

        private static bool CheckProjects(DoctorSettings settings, DoctorReporter reporter)
        {
            try
            {
                string db = ConfigHelper.ResolveDb(settings.Db);
                var repo = new SqliteRepository(db);
                repo.EnsureSchema();
                return CheckProjectPaths(repo.ListProjects(), reporter);
            }
            catch (Exception ex)
            {
                return reporter.Fail($"Could not inspect projects: {ex.Message}");
            }
        }

        private static bool CheckProjectPaths(IEnumerable<Core.Models.Project> projects, DoctorReporter reporter)
        {
            var list = projects.ToList();
            if (list.Count == 0)
                reporter.Warn("No projects registered yet");

            bool ok = true;
            foreach (Core.Models.Project project in list)
            {
                ok &= Directory.Exists(project.RootPath)
                    ? reporter.Pass($"Project path exists: {project.Name} -> {project.RootPath}")
                    : reporter.Fail($"Project path missing: {project.Name} -> {project.RootPath}");
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
                string dest = rawDestination.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                await WriteProbeAsync(dest, cancellationToken);
                return reporter.Pass($"Destination writable: {dest}");
            }
            catch (Exception ex)
            {
                return reporter.Fail($"Destination not writable: {ex.Message}");
            }
        }

        private static async Task WriteProbeAsync(string directory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directory);
            string testFile = Path.Combine(directory, ".vaultsync_write_test");
            await File.WriteAllTextAsync(testFile, "ok", cancellationToken);
            File.Delete(testFile);
        }

        private sealed class DoctorReporter(bool quiet)
        {
            public bool Pass(string msg)
            {
                if (!quiet)
                    AnsiConsole.MarkupLine($"[green]+[/] {Markup.Escape(msg)}");

                return true;
            }

            public bool Fail(string msg)
            {
                if (!quiet)
                    AnsiConsole.MarkupLine($"[red]x[/] {Markup.Escape(msg)}");

                return false;
            }

            public void Warn(string msg)
            {
                if (!quiet)
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(msg)}[/]");
            }
        }
    }
}
