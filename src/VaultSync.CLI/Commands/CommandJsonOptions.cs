using System.Text.Json;

namespace VaultSync.CLI.Commands
{
    static class CommandJsonOptions
    {
        public static JsonSerializerOptions Indented { get; } = new() { WriteIndented = true };
    }
}
