using System;

namespace VaultSync.Core.Models;

public static class BackupModes
{
    public const string Full = "full";
    public const string Incremental = "incremental";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Incremental, StringComparison.OrdinalIgnoreCase))
            return Incremental;

        return Full;
    }
}

