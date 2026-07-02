using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace VaultSync.CLI.Commands
{
    sealed class VersionSettings : CommandSettings;

    sealed class VersionCommand : AsyncCommand<VersionSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, VersionSettings s, CancellationToken cancellationToken)
        {
            var asm = Assembly.GetExecutingAssembly();
            string? informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            string version = informational ?? asm.GetName().Version?.ToString() ?? "0.0.0";
            AnsiConsole.MarkupLine($"VaultSync CLI v{Markup.Escape(version)}");
            return Task.FromResult(0);
        }
    }
}