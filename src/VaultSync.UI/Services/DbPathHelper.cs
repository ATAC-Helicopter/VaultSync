using System;
using System.IO;

namespace VaultSync.UI.Services;

public static class DbPathHelper
{
    public static string Resolve()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cfgDir = Path.Combine(home, ".vaultsync");
        Directory.CreateDirectory(cfgDir);
        // default DB used by CLI unless user changed it; we’ll make this configurable later
        var db = Path.Combine(cfgDir, "vault.db");
        return db;
    }
}