using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Config;

namespace VaultSync.CLI.Commands
{
    sealed class InitSettings : CommandSettings
    {
        [CommandOption("--db")] public string Db { get; init; } = "~/.vaultsync/vault.db";
        [CommandOption("--quiet")] public bool Quiet { get; init; } = false;
    }

    sealed class InitCommand : AsyncCommand<InitSettings>
    {
        public override Task<int> ExecuteAsync(CommandContext context, InitSettings s, CancellationToken ct)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var expanded = s.Db.Replace("~", home);

            ConfigHelper.Save(new AppConfig { Database = expanded });

            if (!s.Quiet)
            {
                var dir = ConfigHelper.GetConfigDir();
                AnsiConsole.MarkupLine($"[green]Initialized config at[/] {Markup.Escape(dir)}");
                var pretty = JsonSerializer.Serialize(new AppConfig { Database = s.Db }, new JsonSerializerOptions { WriteIndented = true });
                AnsiConsole.WriteLine(pretty);
            }

            return Task.FromResult(0);
        }
    }
}