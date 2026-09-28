using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console.Cli;

namespace VaultSync.CLI.Commands;

internal sealed class CompletionSettings : CommandSettings
{
    [CommandArgument(0, "<shell>")]
    public string Shell { get; init; } = string.Empty;
}

internal sealed class CompletionCommand : AsyncCommand<CompletionSettings>
{
    protected override Task<int> ExecuteAsync(
        CommandContext context, CompletionSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? resource = settings.Shell.ToLowerInvariant() switch
        {
            "bash" => "VaultSync.CLI.Completions.vaultsync.bash",
            "zsh" => "VaultSync.CLI.Completions._vaultsync",
            "powershell" or "pwsh" => "VaultSync.CLI.Completions.vaultsync.ps1",
            _ => null
        };
        if (resource is null)
        {
            Console.Error.WriteLine("Choose bash, zsh, or powershell. Run `vaultsync completion --help` for usage.");
            return Task.FromResult(2);
        }

        using Stream? stream = typeof(CompletionCommand).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            Console.Error.WriteLine("The completion script is unavailable in this CLI build.");
            return Task.FromResult(1);
        }

        using var reader = new StreamReader(stream);
        Console.Write(reader.ReadToEnd());
        return Task.FromResult(0);
    }
}
