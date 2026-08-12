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
        protected override Task<int> ExecuteAsync(CommandContext context, DestinationSettings settings, CancellationToken cancellationToken)
        {
            AppConfig config = ConfigHelper.Load();
            List<BackupDestination> destinations = BuildActiveDestinations(config);
            if (destinations.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No backup destinations configured.[/]");
                return Task.FromResult(0);
            }

            List<DestinationInfo> results = BuildDestinationInfo(config, destinations, settings.Test);

            if (settings.Json)
            {
                WriteJson(results);
                return Task.FromResult(0);
            }

            WriteTable(results, settings.Test);
            return Task.FromResult(0);
        }

        private static List<DestinationInfo> BuildDestinationInfo(
            AppConfig config,
            IEnumerable<BackupDestination> destinations,
            bool test)
        {
            NetworkMountService? mountService = test ? new NetworkMountService() : null;
            return [.. destinations.Select(dest => BuildDestinationInfo(config, mountService, dest, test))];
        }

        private static DestinationInfo BuildDestinationInfo(
            AppConfig config,
            NetworkMountService? mountService,
            BackupDestination dest,
            bool test)
        {
            string alias = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path ?? string.Empty : dest.Alias;
            string path = dest.Path ?? string.Empty;
            string status = dest.Active ? "Active" : "Inactive";
            if (!test || !dest.Active)
                return new DestinationInfo(alias, path, status, false, "Configured");

            return TestDestination(config, mountService!, dest, alias, path, status);
        }

        private static DestinationInfo TestDestination(
            AppConfig config,
            NetworkMountService mountService,
            BackupDestination dest,
            string alias,
            string path,
            string status)
        {
            try
            {
                NetworkCredentialProfile? profile = ResolveCredential(config, dest);
                DestinationResolution resolution = mountService.PrepareDestination(dest, profile);
                bool reachable = resolution.IsSuccess;
                string defaultMessage = reachable ? "Reachable" : "Unreachable";
                string message = string.IsNullOrWhiteSpace(resolution.Message)
                    ? defaultMessage
                    : resolution.Message;
                NetworkMountService.Cleanup(resolution);
                return new DestinationInfo(alias, path, status, reachable, message);
            }
            catch (Exception ex)
            {
                return new DestinationInfo(alias, path, status, false, ex.Message);
            }
        }

        private static void WriteJson(IEnumerable<DestinationInfo> results)
        {
            var payload = results.Select(r => new
            {
                r.Alias,
                r.Path,
                r.Status,
                r.Reachable,
                r.Message
            });
            Console.WriteLine(JsonSerializer.Serialize(payload, CommandJsonOptions.Indented));
        }

        private static void WriteTable(IEnumerable<DestinationInfo> results, bool test)
        {
            Table table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Alias");
            table.AddColumn(new TableColumn("Path").Width(35));
            table.AddColumn("Status");
            table.AddColumn("Details");

            foreach (DestinationInfo row in results)
            {
                string testedDetail = row.Reachable ? "Reachable" : row.Message;
                string detail = test
                    ? testedDetail
                    : row.Message;

                table.AddRow(row.Alias, row.Path, row.Status, detail);
            }

            AnsiConsole.MarkupLine("[bold]Configured backup destinations[/]");
            AnsiConsole.Write(table);
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
