using System;
using System.Text.Json;
using Spectre.Console.Cli;

namespace VaultSync.CLI.Commands;

internal static class CommandOutput
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static bool IsJson(string? output) => string.Equals(output, "json", StringComparison.OrdinalIgnoreCase);

    public static string? Validate(string? output, bool legacyJson = false)
    {
        if (output is not null && legacyJson)
            return "Choose either --output or legacy --json, not both.";
        if (output is not null && !IsJson(output) && !string.Equals(output, "text", StringComparison.OrdinalIgnoreCase))
            return "--output must be text or json.";
        return null;
    }

    public static void ConfigureErrors(IConfigurator configuration, string[] arguments)
    {
        string? operation = StructuredOperation(arguments);
        if (operation is null)
            return;
        configuration.UseStrictParsing();
        configuration.SetExceptionHandler((error, _) => error is CommandParseException or CommandRuntimeException
            ? Failure("json", operation, "invalid_options", "Invalid arguments. Check option names and values, or run the command with --help.", 2)
            : Failure("json", operation, "command_failed", "Command could not complete. Check database access and supported schema.", 1));
    }

    private static string? StructuredOperation(string[] arguments)
    {
        if (!RequestsJson(arguments))
            return null;
        if (arguments.Length > 0 && string.Equals(arguments[0], "list-projects", StringComparison.OrdinalIgnoreCase))
            return "projects.list";
        if (arguments.Length > 0 && string.Equals(arguments[0], "history", StringComparison.OrdinalIgnoreCase))
            return "snapshots.list";
        if (arguments.Length > 0 && string.Equals(arguments[0], "snapshot", StringComparison.OrdinalIgnoreCase))
            return "snapshots.create";
        if (arguments.Length > 0 && string.Equals(arguments[0], "diff", StringComparison.OrdinalIgnoreCase))
            return "snapshots.diff";
        if (arguments.Length > 0 && string.Equals(arguments[0], "restore", StringComparison.OrdinalIgnoreCase))
            return "recovery.restore";
        if (arguments.Length < 2)
            return null;
        if (string.Equals(arguments[0], "projects", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(arguments[1], "list", StringComparison.OrdinalIgnoreCase))
                return "projects.list";
            return string.Equals(arguments[1], "show", StringComparison.OrdinalIgnoreCase) ? "projects.show" : null;
        }
        if (string.Equals(arguments[0], "backups", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(arguments[1], "create", StringComparison.OrdinalIgnoreCase))
                return "backups.create";
            if (string.Equals(arguments[1], "list", StringComparison.OrdinalIgnoreCase))
                return "backups.list";
            if (string.Equals(arguments[1], "show", StringComparison.OrdinalIgnoreCase))
                return "backups.show";
            if (string.Equals(arguments[1], "verify-all", StringComparison.OrdinalIgnoreCase))
                return "backups.verify-all";
            return string.Equals(arguments[1], "verify", StringComparison.OrdinalIgnoreCase) ? "backups.verify" : null;
        }
        if (string.Equals(arguments[0], "recovery", StringComparison.OrdinalIgnoreCase))
            return string.Equals(arguments[1], "restore", StringComparison.OrdinalIgnoreCase) ? "recovery.restore" : null;
        if (!string.Equals(arguments[0], "snapshots", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(arguments[1], "create", StringComparison.OrdinalIgnoreCase))
            return "snapshots.create";
        if (string.Equals(arguments[1], "list", StringComparison.OrdinalIgnoreCase))
            return "snapshots.list";
        if (string.Equals(arguments[1], "show", StringComparison.OrdinalIgnoreCase))
            return "snapshots.show";
        return string.Equals(arguments[1], "diff", StringComparison.OrdinalIgnoreCase) ? "snapshots.diff" : null;
    }

    private static bool RequestsJson(string[] arguments)
    {
        for (int index = 0; index < arguments.Length && arguments[index] != "--"; index++)
        {
            if (string.Equals(arguments[index], "--output=json", StringComparison.OrdinalIgnoreCase))
                return true;
            if (arguments[index] == "--output" && index + 1 < arguments.Length && IsJson(arguments[index + 1]))
                return true;
        }
        return false;
    }

    public static int Success(string operation, object data)
    {
        Write(operation, "success", data, null, 0);
        return 0;
    }

    public static int Failure(string? output, string operation, string code, string message, int exitCode)
    {
        if (IsJson(output))
            Write(operation, "error", null, new { code, message }, exitCode);
        else
            Console.Error.WriteLine(message);
        return exitCode;
    }

    public static int FailureWithDetails(string operation, string code, string message, object details, int exitCode)
    {
        Write(operation, "error", null, new { code, message, details }, exitCode);
        return exitCode;
    }

    private static void Write(string operation, string status, object? data, object? error, int exitCode)
        => Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, operation, status, data, error, exitCode }, Options));
}
