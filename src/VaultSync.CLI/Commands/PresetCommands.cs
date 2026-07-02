using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using VaultSync.CLI.Presets;

namespace VaultSync.CLI.Commands
{
    sealed class PresetsListSettings : CommandSettings;
    sealed class PresetsShowSettings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
    }

    sealed class PresetsListCommand : AsyncCommand<PresetsListSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, PresetsListSettings s, CancellationToken cancellationToken)
        {
            IEnumerable<string> names = PresetStore.ListNames();
            Table table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Preset");
            foreach (string n in names) table.AddRow(n);
            AnsiConsole.Write(table);
            return Task.FromResult(0);
        }
    }

    sealed class PresetsShowCommand : AsyncCommand<PresetsShowSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, PresetsShowSettings s, CancellationToken cancellationToken)
        {
            string content = PresetStore.Load(s.Name);
            AnsiConsole.MarkupLine($"[blue]{Markup.Escape(s.Name)}[/] preset:");
            AnsiConsole.WriteLine(content);
            return Task.FromResult(0);
        }
    }
}
