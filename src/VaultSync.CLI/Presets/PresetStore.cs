using System.Text.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".vaultsync", "presets");
        }

        private static string BuiltInPresetsDir()
        {
            // 1) Environment override for power users / testing
            var env = Environment.GetEnvironmentVariable("VAULTSYNC_PRESETS_DIR");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
                return env;

            // 2) Installed / published app: <app>/presets
            var appPresets = Path.Combine(AppContext.BaseDirectory, "presets");
            if (Directory.Exists(appPresets))
                return appPresets;

            // 3) Dev tree: walk up to find src/presets (current repo layout)
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

            // 4) Fallback to app presets path (may or may not exist)
            return appPresets;
        }

        private static PresetIndex? LoadIndex(string dir)
        {
            try
            {
                var indexPath = Path.Combine(dir, "presets.index.json");
                if (!File.Exists(indexPath))
                    return null;

                var json = File.ReadAllText(indexPath);
                var index = JsonSerializer.Deserialize<PresetIndex>(json);
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
            var userDir = UserPresetsDir();
            Directory.CreateDirectory(userDir);

            // User presets: names are file names without extension
            var userFiles = Directory.EnumerateFiles(userDir, "*.vaultsyncignore")
                                     .Select(f => Path.GetFileNameWithoutExtension(f));

            // Built-in presets from index or from files
            var builtInDir = BuiltInPresetsDir();
            IEnumerable<string> builtInNames = Array.Empty<string>();

            if (Directory.Exists(builtInDir))
            {
                var index = LoadIndex(builtInDir);
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
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Preset name cannot be empty.", nameof(name));

            // 1) User override in ~/.vaultsync/presets
            var userDir = UserPresetsDir();
            Directory.CreateDirectory(userDir);
            var userPath = Path.Combine(userDir, $"{name}.vaultsyncignore");
            if (File.Exists(userPath))
                return File.ReadAllText(userPath);

            // 2) Built-in presets
            var builtInDir = BuiltInPresetsDir();
            if (Directory.Exists(builtInDir))
            {
                var index = LoadIndex(builtInDir);

                // Try index first
                if (index?.Presets != null && index.Presets.Count > 0)
                {
                    var preset = index.Presets
                        .FirstOrDefault(p =>
                            string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetFileNameWithoutExtension(p.File), name, StringComparison.OrdinalIgnoreCase));

                    if (preset != null)
                    {
                        var presetPath = Path.Combine(builtInDir, preset.File);
                        if (File.Exists(presetPath))
                            return File.ReadAllText(presetPath);
                    }
                }

                // Fallback: direct file name match without index
                var fallbackPath = Path.Combine(builtInDir, $"{name}.vaultsyncignore");
                if (File.Exists(fallbackPath))
                    return File.ReadAllText(fallbackPath);
            }

            // 3) Not found anywhere
            var builtInAvailable = Directory.Exists(BuiltInPresetsDir())
                ? string.Join(", ",
                    ListNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                : "none";

            throw new Exception(
                $"Preset '{name}' not found. Create '{userPath}' or choose one of: {builtInAvailable}");
        }
    }
}