using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.CLI.Config;

namespace VaultSync.CLI.Commands
{
    sealed class DestinationSettings : CommandSettings
    {
        [CommandOption("--test")] public bool Test { get; init; }
        [CommandOption("--json")] public bool Json { get; init; } = false;
    }

    sealed class DestinationCommand : AsyncCommand<DestinationSettings>
    {
        public override Task<int> ExecuteAsync(CommandContext context, DestinationSettings settings, CancellationToken ct)
        {
            var config = AppConfigStore.Load();
            var destinations = BuildActiveDestinations(config);
            if (destinations.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No backup destinations configured.[/]");
                return Task.FromResult(0);
            }

            NetworkMountService? mountService = null;
            if (settings.Test)
            {
                mountService = new NetworkMountService();
            }

            var results = new List<DestinationInfo>();
            foreach (var dest in destinations)
            {
                var alias = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path ?? string.Empty : dest.Alias;
                var path = dest.Path ?? string.Empty;
                var status = dest.Active ? "Active" : "Inactive";
                var message = "Configured";
                var reachable = false;

                if (settings.Test && dest.Active)
                {
                    var profile = ResolveCredential(config, dest);
                    try
                    {
                        var resolution = mountService!.PrepareDestination(dest, profile);
                        reachable = resolution.IsSuccess;
                        message = string.IsNullOrWhiteSpace(resolution.Message)
                            ? (reachable ? "Reachable" : "Unreachable")
                            : resolution.Message;
                        mountService.Cleanup(resolution);
                    }
                    catch (Exception ex)
                    {
                        reachable = false;
                        message = ex.Message;
                    }
                }

                results.Add(new DestinationInfo(alias, path, status, reachable, message));
            }

            if (settings.Json)
            {
                var payload = results.Select(r => new
                {
                    r.Alias,
                    r.Path,
                    Status = r.Status,
                    Reachable = r.Reachable,
                    r.Message
                });
                var options = new JsonSerializerOptions { WriteIndented = true };
                Console.WriteLine(JsonSerializer.Serialize(payload, options));
                return Task.FromResult(0);
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Alias");
            table.AddColumn(new TableColumn("Path").Width(35));
            table.AddColumn("Status");
            table.AddColumn("Details");

            foreach (var row in results)
            {
                var detail = settings.Test
                    ? (row.Reachable ? "Reachable" : row.Message)
                    : row.Message;

                table.AddRow(row.Alias, row.Path, row.Status, detail);
            }

            AnsiConsole.MarkupLine("[bold]Configured backup destinations[/]");
            AnsiConsole.Write(table);
            return Task.FromResult(0);
        }

        private static List<BackupDestination> BuildActiveDestinations(AppConfig config)
        {
            var list = new List<BackupDestination>();
            if (config.Backups.Destinations is { Count: > 0 })
            {
                list.AddRange(config.Backups.Destinations);
            }
            else if (!string.IsNullOrWhiteSpace(config.Backups.BackupRoot))
            {
                list.Add(new BackupDestination
                {
                    Alias = "Primary",
                    Path = config.Backups.BackupRoot,
                    Active = true,
                    PreMounted = true
                });
            }

            return list;
        }

        private static NetworkCredentialProfile? ResolveCredential(AppConfig config, BackupDestination dest)
        {
            if (string.IsNullOrWhiteSpace(dest.CredentialName))
                return null;

            return config.Network.Credentials?.FirstOrDefault(c =>
                string.Equals(c.Name, dest.CredentialName ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        }
    }

    sealed record DestinationInfo(string Alias, string Path, string Status, bool Reachable, string Message);
}
