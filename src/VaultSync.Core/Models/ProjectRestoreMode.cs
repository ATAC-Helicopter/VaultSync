namespace VaultSync.Core.Models;

public static class ProjectRestoreMode
{
    public const string Direct = "direct";
    public const string Sandbox = "sandbox";

    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return Direct;

        return mode.Trim().ToLowerInvariant() switch
        {
            Sandbox => Sandbox,
            _ => Direct
        };
    }
}

