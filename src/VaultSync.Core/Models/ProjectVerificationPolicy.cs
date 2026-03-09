namespace VaultSync.Core.Models;

public static class ProjectVerificationPolicy
{
    public const string Always = "always";
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";

    public static string Normalize(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
            return Always;

        return policy.Trim().ToLowerInvariant() switch
        {
            Scheduled => Scheduled,
            Manual => Manual,
            _ => Always
        };
    }
}
