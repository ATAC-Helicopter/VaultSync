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
        protected override Task<int> ExecuteAsync(CommandContext context, InitSettings s, CancellationToken ct)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string targetDb = string.IsNullOrWhiteSpace(s.Db)
                ? AppConfigStore.GetDefaultDbPath()
                : s.Db.Replace("~", home);

            AppConfig cfg = AppConfigStore.Load();
            cfg.DbPath = targetDb;
            ConfigHelper.Save(cfg);

            if (!s.Quiet)
            {
                string dir = ConfigHelper.GetConfigDir();
                AnsiConsole.MarkupLine($"[green]Initialized config at[/] {Markup.Escape(dir)}");

                string pretty = JsonSerializer.Serialize(
                    new { DbPath = targetDb },
                    CommandJsonOptions.Indented);
                AnsiConsole.WriteLine(pretty);
            }

            return Task.FromResult(0);
        }
    }
}
