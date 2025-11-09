using System.Text.RegularExpressions;

namespace VaultSync.Core.Services;

public class FilterService
{
    private readonly List<string> _patterns;
    public FilterService(IEnumerable<string> patterns) => _patterns = patterns.Select(Normalize).ToList();

    public static FilterService FromPresetAndLocal(string projectRoot, string presetName, string? presetsDir = null)
    {
        var patterns = new List<string>();

        // Resolve presets directory by priority
        presetsDir = ResolvePresetsDir(presetsDir);

        // Load preset file if present
        if (!string.IsNullOrWhiteSpace(presetName))
        {
            var presetFile = Path.Combine(presetsDir, $"{presetName}.vaultsyncignore");
            if (File.Exists(presetFile))
                patterns.AddRange(ReadLines(presetFile));
        }

        // Merge local project overrides
        var localIgnore = Path.Combine(projectRoot, ".vaultsyncignore");
        if (File.Exists(localIgnore))
            patterns.AddRange(ReadLines(localIgnore));

        return new FilterService(patterns);
    }

    private static string ResolvePresetsDir(string? presetsDir)
    {
        // 1) explicit argument wins
        if (!string.IsNullOrWhiteSpace(presetsDir) && Directory.Exists(presetsDir))
            return presetsDir!;

        // 2) environment override
        var env = Environment.GetEnvironmentVariable("VAULTSYNC_PRESETS_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env!;

        // 3) ~/.vaultsync/presets
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homePresets = Path.Combine(home, ".vaultsync", "presets");
        if (Directory.Exists(homePresets))
            return homePresets;

        // 4) published app: <app>/presets
        var appPresets = Path.Combine(AppContext.BaseDirectory, "presets");
        if (Directory.Exists(appPresets))
            return appPresets;

        // 5) dev tree: walk up to find src/presets
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "src", "presets");
            if (Directory.Exists(candidate))
                return candidate;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            dir = parent;
        }

        // 6) fallback to current working directory
        return Directory.GetCurrentDirectory();
    }

    public bool ShouldExclude(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        foreach (var p in _patterns)
            if (GlobMatch(rel, p)) return true;
        return false;
    }

    private static IEnumerable<string> ReadLines(string file) =>
        File.ReadAllLines(file)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#"));

    private static string Normalize(string s)
    {
        s = s.Replace('\\', '/');
        if (s.EndsWith('/')) s += "*"; // folder rule
        return s;
    }

    private static bool GlobMatch(string text, string pattern)
    {
        var rx = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase);
    }
}