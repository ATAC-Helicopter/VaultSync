using System.Text.Json;
using System.Text.RegularExpressions;

namespace VaultSync.Core.Services;

public class FilterService
{
    private static readonly TimeSpan s_regexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly List<string> _patterns;
    private readonly List<Regex> _compiledPatterns;

    /// <summary>
    /// True when this filter has at least one ignore rule.
    /// </summary>
    public bool HasRules => _patterns.Count > 0;

    /// <summary>
    /// The normalized ignore patterns used by this filter. These patterns use forward
    /// slashes and have directory rules normalized (trailing '/' turned into '/*').
    /// VaultSync-owned backup artifacts are always appended as safety exclusions.
    /// </summary>
    public IReadOnlyList<string> RawPatterns => _patterns;

    public FilterService(IEnumerable<string> patterns)
    {
        _patterns = BackupSafetyService.AddReservedIgnorePatterns(patterns)
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _compiledPatterns = _patterns.Select(CompilePattern).ToList();
    }

    public static FilterService FromPresetAndLocal(
        string projectRoot,
        string presetName,
        string? presetsDir = null,
        IVaultLogger? logger = null)
    {
        logger ??= RuntimeVaultLogger.Instance;
        var patterns = new List<string>();

        // Resolve presets directory by priority
        presetsDir = ResolvePresetsDir(presetsDir);

        logger.Info($"[FilterService] Using presetsDir='{presetsDir}', preset='{presetName}'");

        // Load preset file if present
        if (!string.IsNullOrWhiteSpace(presetName))
        {
            string? presetFile = ResolvePresetFile(presetsDir, presetName);
            if (string.IsNullOrWhiteSpace(presetFile))
                throw new FileNotFoundException(
                    $"Requested exclusion preset '{presetName}' could not be resolved; refusing to continue without its rules.");

            // If the file disappears or becomes unreadable after resolution,
            // propagate the I/O error instead of silently omitting exclusions.
            IReadOnlyList<string> presetLines = ReadLinesFresh(presetFile);
            logger.Info($"[FilterService] Loaded {presetLines.Count} rules from preset file.");
            patterns.AddRange(presetLines);
        }

        // Merge local project overrides
        string localIgnore = Path.Combine(projectRoot, ".vaultsyncignore");
        if (File.Exists(localIgnore))
        {
            IReadOnlyList<string> localLines = ReadLinesFresh(localIgnore);
            logger.Info($"[FilterService] Loaded {localLines.Count} rules from local .vaultsyncignore.");
            patterns.AddRange(localLines);
        }

        logger.Info($"[FilterService] Total rules in combined filter: {patterns.Count} user/preset + {BackupSafetyService.ReservedIgnorePatterns.Count} reserved safety rules");

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
        string indexPath = Path.Combine(presetsDir, "presets.index.json");
        if (!File.Exists(indexPath))
            return null;

        try
        {
            Dictionary<string, string> byId = ReadPresetIndex(indexPath);

            if (!byId.TryGetValue(presetName, out string? fileName) || string.IsNullOrWhiteSpace(fileName))
                return null;

            string mappedPath = Path.Combine(presetsDir, fileName);
            return File.Exists(mappedPath) ? mappedPath : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                "Requested exclusion preset index could not be read or parsed; refusing to omit the intended exclusion policy.",
                ex);
        }
    }

    private static Dictionary<string, string> ReadPresetIndex(string indexPath)
    {
        string json = File.ReadAllText(indexPath);
        PresetIndex? index = JsonSerializer.Deserialize<PresetIndex>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PresetInfo preset in (index?.Presets ?? [])
                     .Where(preset => !string.IsNullOrWhiteSpace(preset.Id) &&
                                      !string.IsNullOrWhiteSpace(preset.File)))
        {
            map[preset.Id] = preset.File;
        }

        return map;
    }

    private static string ResolvePresetsDir(string? presetsDir)
    {
        // 1) explicit argument wins
        if (!string.IsNullOrWhiteSpace(presetsDir) && Directory.Exists(presetsDir))
            return presetsDir;

        // 2) environment override for power users
        string? env = Environment.GetEnvironmentVariable("VAULTSYNC_PRESETS_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

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
        if (BackupSafetyService.IsReservedPath(root, fullPath))
            return true;

        var rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return _compiledPatterns.Any(rx => rx.IsMatch(rel));
    }

    private static IEnumerable<string> ReadLines(string file) =>
        File.ReadAllLines(file)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'));

    // The exclusion policy may change even when tools preserve file timestamps.
    // Reading a short ignore file on each new filter construction avoids stale
    // backup inclusion/exclusion decisions based on timestamp-only cache keys.
    private static IReadOnlyList<string> ReadLinesFresh(string file) =>
        ReadLines(file).ToArray();

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
        return new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, s_regexTimeout);
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
