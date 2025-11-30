using System.Text.RegularExpressions;

namespace VaultSync.Core.Services;

public class FilterService
{
    private readonly List<string> _patterns;

    /// <summary>
    /// True when this filter has at least one ignore rule.
    /// </summary>
    public bool HasRules => _patterns.Count > 0;

    /// <summary>
    /// The normalized ignore patterns used by this filter. These patterns use forward
    /// slashes and have directory rules normalized (trailing '/' turned into '/*').
    /// </summary>
    public IReadOnlyList<string> RawPatterns => _patterns;
    public FilterService(IEnumerable<string> patterns) => _patterns = patterns.Select(Normalize).ToList();

    public static FilterService FromPresetAndLocal(string projectRoot, string presetName, string? presetsDir = null)
    {
        var patterns = new List<string>();

        // Resolve presets directory by priority
        presetsDir = ResolvePresetsDir(presetsDir);

        Console.WriteLine($"[FilterService] Using presetsDir='{presetsDir}', preset='{presetName}'");

        // Load preset file if present
        if (!string.IsNullOrWhiteSpace(presetName))
        {
            var presetFile = Path.Combine(presetsDir, $"{presetName}.vaultsyncignore");
            Console.WriteLine($"[FilterService] Looking for preset file '{presetFile}'");

            if (File.Exists(presetFile))
            {
                var presetLines = ReadLines(presetFile).ToList();
                Console.WriteLine($"[FilterService] Loaded {presetLines.Count} rules from preset file.");
                patterns.AddRange(presetLines);
            }
            else
            {
                Console.WriteLine("[FilterService] Preset file NOT FOUND.");
            }
        }

        // Merge local project overrides
        var localIgnore = Path.Combine(projectRoot, ".vaultsyncignore");
        if (File.Exists(localIgnore))
        {
            var localLines = ReadLines(localIgnore).ToList();
            Console.WriteLine($"[FilterService] Loaded {localLines.Count} rules from local .vaultsyncignore.");
            patterns.AddRange(localLines);
        }

        Console.WriteLine($"[FilterService] Total rules in combined filter: {patterns.Count}");

        return new FilterService(patterns);
    }

    private static string ResolvePresetsDir(string? presetsDir)
    {
        // 1) explicit argument wins
        if (!string.IsNullOrWhiteSpace(presetsDir) && Directory.Exists(presetsDir))
            return presetsDir!;

        // 2) environment override for power users
        var env = Environment.GetEnvironmentVariable("VAULTSYNC_PRESETS_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env!;

        // 3) dev tree: walk up to find src/presets (current repo layout)
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "src", "presets");
            if (Directory.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null)
                break;

            dir = parent;
        }

        // 4) installed/published app: <app>/presets (next to executable)
        var appPresets = Path.Combine(AppContext.BaseDirectory, "presets");
        if (Directory.Exists(appPresets))
            return appPresets;

        // 5) fallback to current working directory
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

        // Treat patterns ending in '/' as directory rules that should match the directory
        // and all files/folders beneath it. Using "**" ensures the generated regex will
        // match any depth, not just immediate children.
        if (s.EndsWith('/'))
            s += "**";

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