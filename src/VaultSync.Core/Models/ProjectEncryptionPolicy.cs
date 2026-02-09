namespace VaultSync.Core.Models;

public static class ProjectEncryptionPolicy
{
    public const string Inherit = "inherit";
    public const string Encrypted = "encrypted";
    public const string Plain = "plain";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Inherit;

        return value.Trim().ToLowerInvariant() switch
        {
            Encrypted => Encrypted,
            Plain => Plain,
            _ => Inherit
        };
    }

    public static bool IsEncrypted(string? policy, bool globalEnabled)
    {
        return Normalize(policy) switch
        {
            Encrypted => true,
            Plain => false,
            _ => globalEnabled
        };
    }
}

