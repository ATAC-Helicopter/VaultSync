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
        public override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings s, CancellationToken ct)
        {
            var ok = true;
            void Pass(string msg) { if (!s.Quiet) AnsiConsole.MarkupLine($"[green]✔[/] {Markup.Escape(msg)}"); }
            void Fail(string msg) { ok = false; if (!s.Quiet) AnsiConsole.MarkupLine($"[red]✘[/] {Markup.Escape(msg)}"); }

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
                    using var proc = System.Diagnostics.Process.Start(p)!;
                    await proc.WaitForExitAsync(ct);
                    if (proc.ExitCode <= 16) Pass("robocopy found (Windows sync runner)");
                    else Fail("robocopy returned unexpected exit");
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
                    using var proc = System.Diagnostics.Process.Start(p)!;
                    var txt = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync(ct);
                    if (proc.ExitCode == 0 && txt.Contains("rsync", StringComparison.OrdinalIgnoreCase))
                        Pass("rsync found (Unix sync runner)");
                    else
                        Fail("rsync not available or returned non-zero");
                }
            }
            catch { Fail("Platform sync tool not found on PATH"); }

            try
            {
                var db = ConfigHelper.ResolveDb(s.Db);
                var dir = Path.GetDirectoryName(db)!;
                Directory.CreateDirectory(dir);
                var testFile = Path.Combine(dir, ".vaultsync_write_test");
                await File.WriteAllTextAsync(testFile, "ok", ct);
                File.Delete(testFile);
                Pass($"Database path writable: {db}");
            }
            catch (Exception ex) { Fail($"Database path not writable: {ex.Message}"); }

            try
            {
                var db = ConfigHelper.ResolveDb(s.Db);
                var repo = new SqliteRepository(db);
                repo.EnsureSchema();
                var projects = repo.ListProjects();
                if (!projects.Any()) { if (!s.Quiet) AnsiConsole.MarkupLine("[yellow]No projects registered yet[/]"); }
                foreach (var p in projects)
                    if (Directory.Exists(p.RootPath)) Pass($"Project path exists: {p.Name} → {p.RootPath}");
                    else Fail($"Project path missing: {p.Name} → {p.RootPath}");
            }
            catch (Exception ex) { Fail($"Could not inspect projects: {ex.Message}"); }

            if (!string.IsNullOrWhiteSpace(s.CheckDest))
            {
                try
                {
                    var dest = s.CheckDest.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    Directory.CreateDirectory(dest);
                    var test = Path.Combine(dest, ".vaultsync_write_test");
                    await File.WriteAllTextAsync(test, "ok", ct);
                    File.Delete(test);
                    Pass($"Destination writable: {dest}");
                }
                catch (Exception ex) { Fail($"Destination not writable: {ex.Message}"); }
            }

            if (!s.Quiet)
                AnsiConsole.MarkupLine(ok ? "[green]Doctor: all good[/]" : "[red]Doctor: issues found[/]");

            return ok ? 0 : 2;
        }
    }
}