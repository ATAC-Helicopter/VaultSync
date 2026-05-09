using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace VaultSync.Core.Services;

public class FilterService
{
    private readonly List<string> _patterns;
    private readonly List<Regex> _compiledPatterns;
    private static readonly ConcurrentDictionary<string, CachedLines> s_linesCache = new();
    private static readonly ConcurrentDictionary<string, CachedPresetIndex> s_presetIndexCache = new();

    private sealed record CachedLines(DateTime LastWriteUtc, IReadOnlyList<string> Lines);
    private sealed record CachedPresetIndex(DateTime LastWriteUtc, Dictionary<string, string> ById);

    /// <summary>
    /// True when this filter has at least one ignore rule.
    /// </summary>
    public bool HasRules => _patterns.Count > 0;

    /// <summary>
    /// The normalized ignore patterns used by this filter. These patterns use forward
    /// slashes and have directory rules normalized (trailing '/' turned into '/*').
    /// </summary>
    public IReadOnlyList<string> RawPatterns => _patterns;

    public FilterService(IEnumerable<string> patterns)
    {
        _patterns = [.. patterns.Select(Normalize)];
        _compiledPatterns = [.. _patterns.Select(CompilePattern)];
    }

    public static FilterService FromPresetAndLocal(string projectRoot, string presetName, string? presetsDir = null)
    {
        var patterns = new List<string>();

        // Resolve presets directory by priority
        presetsDir = ResolvePresetsDir(presetsDir);

        Console.WriteLine($"[FilterService] Using presetsDir='{presetsDir}', preset='{presetName}'");

        // Load preset file if present
        if (!string.IsNullOrWhiteSpace(presetName))
        {
            string? presetFile = ResolvePresetFile(presetsDir, presetName);
            Console.WriteLine($"[FilterService] Looking for preset file '{presetFile ?? "(not found)"}'");

            if (!string.IsNullOrWhiteSpace(presetFile) && File.Exists(presetFile))
            {
                IReadOnlyList<string> presetLines = ReadLinesCached(presetFile);
                Console.WriteLine($"[FilterService] Loaded {presetLines.Count} rules from preset file.");
                patterns.AddRange(presetLines);
            }
            else
            {
                Console.WriteLine("[FilterService] Preset file NOT FOUND.");
            }
        }

        // Merge local project overrides
        string localIgnore = Path.Combine(projectRoot, ".vaultsyncignore");
        if (File.Exists(localIgnore))
        {
            IReadOnlyList<string> localLines = ReadLinesCached(localIgnore);
            Console.WriteLine($"[FilterService] Loaded {localLines.Count} rules from local .vaultsyncignore.");
            patterns.AddRange(localLines);
        }

        Console.WriteLine($"[FilterService] Total rules in combined filter: {patterns.Count}");

        return new FilterService(patterns);
    }

    private static string? ResolvePresetFile(string presetsDir, string presetName)
    {
        string directPath = Path.Combine(presetsDir, $"{presetName}.vaultsyncignore");
        if (File.Exists(directPath))
            return directPath;

        // Use the index mapping when preset IDs and file names differ
        // (for example `cpp` -> `c_cpp.vaultsyncignore`).
        string? mappedFile = ResolvePresetFileFromIndex(presetsDir, presetName);
        if (!string.IsNullOrWhiteSpace(mappedFile))
            return mappedFile;

        return null;
    }

    private static string? ResolvePresetFileFromIndex(string presetsDir, string presetName)
    {
        try
        {
            string indexPath = Path.Combine(presetsDir, "presets.index.json");
            if (!File.Exists(indexPath))
                return null;

            DateTime lastWrite = File.GetLastWriteTimeUtc(indexPath);
            if (!s_presetIndexCache.TryGetValue(indexPath, out CachedPresetIndex? cached) || cached.LastWriteUtc != lastWrite)
            {
                string json = File.ReadAllText(indexPath);
                PresetIndex? index = JsonSerializer.Deserialize<PresetIndex>(json);
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (index?.Presets != null)
                {
                    foreach (PresetInfo preset in index.Presets)
                    {
                        if (string.IsNullOrWhiteSpace(preset.Id) || string.IsNullOrWhiteSpace(preset.File))
                            continue;

                        map[preset.Id] = preset.File;
                    }
                }

                cached = new CachedPresetIndex(lastWrite, map);
                s_presetIndexCache[indexPath] = cached;
            }

            if (!cached.ById.TryGetValue(presetName, out string? fileName) || string.IsNullOrWhiteSpace(fileName))
                return null;

            string mappedPath = Path.Combine(presetsDir, fileName);
            return File.Exists(mappedPath) ? mappedPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolvePresetsDir(string? presetsDir)
    {
        // 1) explicit argument wins
        if (!string.IsNullOrWhiteSpace(presetsDir) && Directory.Exists(presetsDir))
            return presetsDir!;

        // 2) environment override for power users
        string? env = Environment.GetEnvironmentVariable("VAULTSYNC_PRESETS_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env!;

        // 3) dev tree: walk up to find src/presets (current repo layout)
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            string candidate = Path.Combine(dir, "src", "presets");
            if (Directory.Exists(candidate))
                return candidate;

            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent is null)
                break;

            dir = parent;
        }

        // 4) installed/published app: <app>/presets (next to executable)
        string appPresets = Path.Combine(AppContext.BaseDirectory, "presets");
        if (Directory.Exists(appPresets))
            return appPresets;

        // 5) fallback to current working directory
        return Directory.GetCurrentDirectory();
    }

    public bool ShouldExclude(string root, string fullPath)
    {
        string rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        foreach (Regex rx in _compiledPatterns)
            if (rx.IsMatch(rel)) return true;
        return false;
    }

    private static IEnumerable<string> ReadLines(string file) =>
        File.ReadAllLines(file)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#"));

    private static IReadOnlyList<string> ReadLinesCached(string file)
    {
        DateTime lastWrite = File.GetLastWriteTimeUtc(file);
        if (s_linesCache.TryGetValue(file, out CachedLines? cached) && cached.LastWriteUtc == lastWrite)
            return cached.Lines;

        var lines = ReadLines(file).ToList();
        s_linesCache[file] = new CachedLines(lastWrite, lines);
        return lines;
    }

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

    private static Regex CompilePattern(string pattern)
    {
        string rx = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", ".") + "$";
        return new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed class PresetIndex
    {
        public List<PresetInfo> Presets { get; set; } = new();
    }

    private sealed class PresetInfo
    {
        public string Id { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
    }
}
