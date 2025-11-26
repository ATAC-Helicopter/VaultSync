using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Config;
using VaultSync.Core.Config;

namespace VaultSync.CLI.Commands
{
    sealed class InitSettings : CommandSettings
    {
        [CommandOption("--db")] public string? Db { get; init; }
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
    }

    sealed class InitCommand : AsyncCommand<InitSettings>
    {
        public override Task<int> ExecuteAsync(CommandContext context, InitSettings s, CancellationToken ct)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var targetDb = string.IsNullOrWhiteSpace(s.Db)
                ? AppConfigStore.GetDefaultDbPath()
                : s.Db.Replace("~", home);

            var cfg = AppConfigStore.Load();
            cfg.DbPath = targetDb;
            ConfigHelper.Save(cfg);

            if (!s.Quiet)
            {
                var dir = ConfigHelper.GetConfigDir();
                AnsiConsole.MarkupLine($"[green]Initialized config at[/] {Markup.Escape(dir)}");

                var pretty = JsonSerializer.Serialize(
                    new { DbPath = targetDb },
                    new JsonSerializerOptions { WriteIndented = true });
                AnsiConsole.WriteLine(pretty);
            }

            return Task.FromResult(0);
        }
    }
}
