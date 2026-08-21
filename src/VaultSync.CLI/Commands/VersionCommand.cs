using System.Threading;
using System.Threading.Tasks;
using Spectre.Console.Cli;
using VaultSync.Core.Services;

namespace VaultSync.CLI.Commands
{
    public sealed class VersionSettings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    public sealed class VersionCommand : AsyncCommand<VersionSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, VersionSettings s, CancellationToken cancellationToken)
        {
            Write(s.Json);
            return Task.FromResult(0);
        }

        public static void Write(bool json)
        {
            BuildInformation information = BuildInformationService.Create(typeof(VersionCommand).Assembly);
            Console.WriteLine(json ? information.ToJson(indented: true) : information.ToDisplayText());
        }
    }
}
