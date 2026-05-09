using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Config;

namespace VaultSync.CLI.Commands
{
    sealed class ConfigShowSettings : CommandSettings;
    sealed class ConfigPathSettings : CommandSettings;

    sealed class ConfigShowCommand : AsyncCommand<ConfigShowSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, ConfigShowSettings settings, CancellationToken ct)
        {
            Core.Config.AppConfig cfg = ConfigHelper.Load();
            string json = JsonSerializer.Serialize(cfg, CommandJsonOptions.Indented);
            Console.WriteLine(json);
            return Task.FromResult(0);
        }
    }

    sealed class ConfigPathCommand : AsyncCommand<ConfigPathSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, ConfigPathSettings settings, CancellationToken ct)
        {
            Core.Config.AppConfig cfg = ConfigHelper.Load();
            Console.WriteLine(string.IsNullOrWhiteSpace(cfg.DbPath)
                ? ConfigHelper.ResolveDb(null)
                : cfg.DbPath);
            return Task.FromResult(0);
        }
    }

    sealed class ConfigSetDbSettings : CommandSettings
    {
        [CommandArgument(0, "<dbPath>")] public string DbPath { get; init; } = "";
    }

    sealed class ConfigSetDbCommand : AsyncCommand<ConfigSetDbSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, ConfigSetDbSettings s, CancellationToken ct)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string expanded = s.DbPath.Replace("~", home);
            string dir = System.IO.Path.GetDirectoryName(expanded)!;
            System.IO.Directory.CreateDirectory(dir);

            Core.Config.AppConfig cfg = ConfigHelper.Load();
            cfg.DbPath = s.DbPath;
            ConfigHelper.Save(cfg);

            AnsiConsole.MarkupLine($"[green]Updated[/] config: database -> {Markup.Escape(s.DbPath)}");
            return Task.FromResult(0);
        }
    }
}

