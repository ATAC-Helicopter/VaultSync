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
            bool ok = true;
            void Pass(string msg) { if (!s.Quiet) AnsiConsole.MarkupLine($"[green]+[/] {Markup.Escape(msg)}"); }
            bool Fail(string msg)
            {
                if (!s.Quiet)
                    AnsiConsole.MarkupLine($"[red]x[/] {Markup.Escape(msg)}");

                return false;
            }

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var p = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "robocopy",
                        ArgumentList = { "/?" },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(p)!;
                    await proc.WaitForExitAsync(cancellationToken);
                    if (proc.ExitCode <= 16) Pass("robocopy found (Windows sync runner)");
                    else ok = Fail("robocopy returned unexpected exit");
                }
                else
                {
                    var p = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "rsync",
                        ArgumentList = { "--version" },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(p)!;
                    string txt = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
                    await proc.WaitForExitAsync(cancellationToken);
                    if (proc.ExitCode == 0 && txt.Contains("rsync", StringComparison.OrdinalIgnoreCase))
                        Pass("rsync found (Unix sync runner)");
                    else
                        ok = Fail("rsync not available or returned non-zero");
                }
            }
            catch { ok = Fail("Platform sync tool not found on PATH"); }

            try
            {
                string db = ConfigHelper.ResolveDb(s.Db);
                string dir = Path.GetDirectoryName(db)!;
                Directory.CreateDirectory(dir);
                string testFile = Path.Combine(dir, ".vaultsync_write_test");
                await File.WriteAllTextAsync(testFile, "ok", cancellationToken);
                File.Delete(testFile);
                Pass($"Database path writable: {db}");
            }
            catch (Exception ex) { ok = Fail($"Database path not writable: {ex.Message}"); }

            try
            {
                string db = ConfigHelper.ResolveDb(s.Db);
                var repo = new SqliteRepository(db);
                repo.EnsureSchema();
                IEnumerable<Core.Models.Project> projects = repo.ListProjects();
                if (!projects.Any()) { if (!s.Quiet) AnsiConsole.MarkupLine("[yellow]No projects registered yet[/]"); }
                foreach (Core.Models.Project p in projects)
                {
                    if (Directory.Exists(p.RootPath)) Pass($"Project path exists: {p.Name} -> {p.RootPath}");
                    else ok = Fail($"Project path missing: {p.Name} -> {p.RootPath}");
                }
            }
            catch (Exception ex) { ok = Fail($"Could not inspect projects: {ex.Message}"); }

            if (!string.IsNullOrWhiteSpace(s.CheckDest))
            {
                try
                {
                    string dest = s.CheckDest.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    Directory.CreateDirectory(dest);
                    string test = Path.Combine(dest, ".vaultsync_write_test");
                    await File.WriteAllTextAsync(test, "ok", cancellationToken);
                    File.Delete(test);
                    Pass($"Destination writable: {dest}");
                }
                catch (Exception ex) { ok = Fail($"Destination not writable: {ex.Message}"); }
            }

            if (!s.Quiet)
                AnsiConsole.MarkupLine(ok ? "[green]Doctor: all good[/]" : "[red]Doctor: issues found[/]");

            return ok ? 0 : 2;
        }
    }
}
