using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VaultSync.CLI.Presets
{
    static class PresetStore
    {
        private static string PresetsDir()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".vaultsync", "presets");
        }

        public static IEnumerable<string> ListNames()
        {
            var dir = PresetsDir();
            Directory.CreateDirectory(dir);
            var userFiles = Directory.EnumerateFiles(dir, "*.vaultsyncignore")
                                     .Select(f => Path.GetFileNameWithoutExtension(f));

            var builtIns = BuiltIn().Keys;
            return userFiles.Union(builtIns, StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        }

        public static string Load(string name)
        {
            var userPath = Path.Combine(PresetsDir(), $"{name}.vaultsyncignore");
            if (File.Exists(userPath))
                return File.ReadAllText(userPath);

            var built = BuiltIn();
            if (built.TryGetValue(name, out var content))
                return content;

            throw new Exception($"Preset '{name}' not found. Put a file at {userPath} or use a built-in: {string.Join(", ", built.Keys.OrderBy(k => k))}");
        }

        private static Dictionary<string,string> BuiltIn()
        {
            var unity = string.Join('\n', new[]
            {
                "Library/","Temp/","Obj/","Build/","Builds/","Logs/",
                "*.csproj","*.sln","*.user","*.unitypackage"
            });

            var dotnet = string.Join('\n', new[]
            {
                "bin/","obj/","*.user","*.suo","*.userprefs",".vs/",
            });

            var blender = string.Join('\n', new[]
            {
                "*.blend1","*.blend2","*.blend@([0-9])","*.blend@([0-9][0-9])","__pycache__/","*.pyc"
            });

            return new(StringComparer.OrdinalIgnoreCase)
            {
                ["unity"]= unity + "\n",
                ["dotnet"]= dotnet + "\n",
                ["blender"]= blender + "\n"
            };
        }
    }
}