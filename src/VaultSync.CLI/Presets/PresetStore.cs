using System.Text.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VaultSync.Core.Services;

namespace VaultSync.CLI.Presets
{
    static class PresetStore
    {
        private sealed class PresetIndex
        {
            public List<PresetInfo> Presets { get; set; } = new();
        }

        private sealed class PresetInfo
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public string File { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
        }

        private static string UserPresetsDir()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".vaultsync", "presets");
        }

        private static string BuiltInPresetsDir()
        {
            // 1) Environment override for power users / testing
            string? env = Environment.GetEnvironmentVariable("VAULTSYNC_PRESETS_DIR");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
                return env;

            // 2) Installed / published app: <app>/presets
            string appPresets = Path.Combine(AppContext.BaseDirectory, "presets");
            if (Directory.Exists(appPresets))
                return appPresets;

            // 3) Dev tree: walk up to find src/presets (current repo layout)
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

            // 4) Fallback to app presets path (may or may not exist)
            return appPresets;
        }

        private static PresetIndex? LoadIndex(string dir)
        {
            try
            {
                string indexPath = Path.Combine(dir, "presets.index.json");
                if (!File.Exists(indexPath))
                    return null;

                string json = File.ReadAllText(indexPath);
                PresetIndex? index = JsonSerializer.Deserialize<PresetIndex>(json);
                return index;
            }
            catch
            {
                // If index is malformed, just treat as if there is no index and fall back to file enumeration.
                return null;
            }
        }

        public static IEnumerable<string> ListNames()
        {
            string userDir = UserPresetsDir();
            Directory.CreateDirectory(userDir);

            // User presets: names are file names without extension
            IEnumerable<string> userFiles = Directory.EnumerateFiles(userDir, "*.vaultsyncignore")
                                     .Select(f => Path.GetFileNameWithoutExtension(f));

            // Built-in presets from index or from files
            string builtInDir = BuiltInPresetsDir();
            IEnumerable<string> builtInNames = Array.Empty<string>();

            if (Directory.Exists(builtInDir))
            {
                PresetIndex? index = LoadIndex(builtInDir);
                if (index?.Presets != null && index.Presets.Count > 0)
                {
                    builtInNames = index.Presets
                        .Select(p => string.IsNullOrWhiteSpace(p.Id)
                            ? Path.GetFileNameWithoutExtension(p.File)
                            : p.Id);
                }
                else
                {
                    builtInNames = Directory.EnumerateFiles(builtInDir, "*.vaultsyncignore")
                        .Select(f => Path.GetFileNameWithoutExtension(f));
                }
            }

            return userFiles
                .Union(builtInNames, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        }

        public static string Load(string name)
        {
            ValidatePresetName(name);

            // 1) User override in ~/.vaultsync/presets
            string userDir = UserPresetsDir();
            Directory.CreateDirectory(userDir);
            string userRelativePath = $"{name}.vaultsyncignore";
            if (TryReadPreset(userDir, userRelativePath, out string userContent))
                return userContent;

            // 2) Built-in presets
            string builtInDir = BuiltInPresetsDir();
            if (TryReadBuiltInPreset(builtInDir, name, out string builtInContent))
                return builtInContent;

            // 3) Not found anywhere
            string builtInAvailable = Directory.Exists(BuiltInPresetsDir())
                ? string.Join(", ",
                    ListNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                : "none";

            string userPath = Path.Combine(userDir, userRelativePath);
            throw new FileNotFoundException(
                $"Preset '{name}' not found. Create '{userPath}' or choose one of: {builtInAvailable}",
                userPath);
        }

        private static bool TryReadBuiltInPreset(string directory, string name, out string content)
        {
            content = string.Empty;
            if (!Directory.Exists(directory))
                return false;

            PresetInfo? preset = LoadIndex(directory)?.Presets.FirstOrDefault(item =>
                string.Equals(item.Id, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(item.File), name, StringComparison.OrdinalIgnoreCase));
            if (preset is not null && TryReadPreset(directory, preset.File, out content))
                return true;

            return TryReadPreset(directory, $"{name}.vaultsyncignore", out content);
        }

        internal static bool TryReadPreset(string directory, string relativePath, out string content)
        {
            content = string.Empty;
            if (!BackupSafetyService.TryResolveExistingFileUnderRoot(directory, relativePath, out string path))
                return false;

            content = File.ReadAllText(path);
            return true;
        }

        private static void ValidatePresetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Preset name cannot be empty.", nameof(name));
            if (!string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) ||
                name.Contains('/') ||
                name.Contains('\\') ||
                name is "." or ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("Preset name must be a file name without path components.", nameof(name));
            }
        }
    }
}
