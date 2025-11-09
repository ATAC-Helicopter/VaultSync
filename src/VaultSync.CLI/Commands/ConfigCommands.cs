using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Config;

namespace VaultSync.CLI.Commands
{
    sealed class ConfigShowSettings : CommandSettings { }
    sealed class ConfigPathSettings : CommandSettings { }

    sealed class ConfigShowCommand : AsyncCommand<ConfigShowSettings>
    {
        public override Task<int> ExecuteAsync(CommandContext context, ConfigShowSettings settings, CancellationToken ct)
        {
            var cfg = ConfigHelper.Load();
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            return Task.FromResult(0);
        }
    }

    sealed class ConfigPathCommand : AsyncCommand<ConfigPathSettings>
    {
        public override Task<int> ExecuteAsync(CommandContext context, ConfigPathSettings settings, CancellationToken ct)
        {
            var cfg = ConfigHelper.Load();
            Console.WriteLine(cfg.Database);
            return Task.FromResult(0);
        }
    }

    sealed class ConfigSetDbSettings : CommandSettings
    {
        [CommandArgument(0, "<dbPath>")] public string DbPath { get; init; } = "";
    }

    sealed class ConfigSetDbCommand : AsyncCommand<ConfigSetDbSettings>
    {
        public override Task<int> ExecuteAsync(CommandContext context, ConfigSetDbSettings s, CancellationToken ct)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var expanded = s.DbPath.Replace("~", home);
            var dir = System.IO.Path.GetDirectoryName(expanded)!;
            System.IO.Directory.CreateDirectory(dir);

            var cfg = ConfigHelper.Load();
            cfg.Database = s.DbPath;
            ConfigHelper.Save(cfg);

            AnsiConsole.MarkupLine($"[green]Updated[/] config: database → {Markup.Escape(s.DbPath)}");
            return Task.FromResult(0);
        }
    }
}