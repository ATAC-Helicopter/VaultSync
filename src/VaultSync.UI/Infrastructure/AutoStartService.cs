using System;
using System.IO;
using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security;
using System.Diagnostics;
using System.Linq;

namespace VaultSync.UI.Infrastructure
{
    public static class AutoStartService
    {
        private const string WindowsRunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string AppId = "VaultSync";

        public static void SetLaunchOnLogin(bool enable)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    SetWindowsAutoStart(enable);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    SetMacAutoStart(enable);
                }
                else if (OperatingSystem.IsLinux())
                {
                    SetLinuxAutoStart(enable);
                }
            }
            catch (Exception ex)
            {
            }
        }

        private static string GetExecutablePath()
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            }
            return path;
        }

        [SupportedOSPlatform("windows")]
        private static void SetWindowsAutoStart(bool enable)
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: true)
                           ?? Registry.CurrentUser.CreateSubKey(WindowsRunKey);
            if (key is null)
                return;

            if (enable)
            {
                var exe = GetExecutablePath();
                if (string.IsNullOrWhiteSpace(exe))
                    return;
                key.SetValue(AppId, $"\"{exe}\"");
            }
            else
            {
                if (key.GetValue(AppId) != null)
                    key.DeleteValue(AppId, throwOnMissingValue: false);
            }
        }

        private static void SetMacAutoStart(bool enable)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            var agentDir = Path.Combine(home, "Library", "LaunchAgents");
            var plistPath = Path.Combine(agentDir, "com.vaultsync.autostart.plist");

            if (!enable)
            {
                if (File.Exists(plistPath))
                {
                    TryLaunchCtl("bootout", plistPath);
                    File.Delete(plistPath);
                }
                return;
            }

            Directory.CreateDirectory(agentDir);
            var args = GetMacLaunchArguments();
            if (args.Length == 0)
                return;

            var programArgs = string.Join(
                Environment.NewLine,
                args.Select(arg => $"      <string>{XmlEscape(arg)}</string>"));
            var plist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
  <dict>
    <key>Label</key><string>com.vaultsync.autostart</string>
    <key>ProgramArguments</key>
    <array>
{programArgs}
    </array>
    <key>RunAtLoad</key><true/>
  </dict>
</plist>";
            File.WriteAllText(plistPath, plist);
            TryLaunchCtl("bootstrap", plistPath);
        }

        private static string[] GetMacLaunchArguments()
        {
            var exe = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(exe))
                return Array.Empty<string>();

            var baseDir = AppContext.BaseDirectory;
            var dllPath = Path.Combine(baseDir, "VaultSync.UI.dll");

            if (exe.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) && File.Exists(dllPath))
            {
                return new[] { exe, dllPath };
            }

            if (File.Exists(exe))
            {
                return new[] { exe };
            }

            if (File.Exists(dllPath))
            {
                return new[] { "dotnet", dllPath };
            }

            return Array.Empty<string>();
        }

        private static void TryLaunchCtl(string verb, string plistPath)
        {
            try
            {
                var uid = Environment.GetEnvironmentVariable("UID");
                if (string.IsNullOrWhiteSpace(uid))
                {
                    using var id = Process.Start(new ProcessStartInfo
                    {
                        FileName = "id",
                        Arguments = "-u",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    uid = id?.StandardOutput.ReadToEnd().Trim();
                }

                if (string.IsNullOrWhiteSpace(uid))
                    return;

                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "launchctl",
                    Arguments = $"{verb} gui/{uid} \"{plistPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                proc?.WaitForExit(2000);
            }
            catch
            {
                // Swallow errors; LaunchAgent will still load on next login.
            }
        }

        private static string XmlEscape(string value)
        {
            return SecurityElement.Escape(value) ?? string.Empty;
        }

        private static void SetLinuxAutoStart(bool enable)
        {
            var configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(configDir))
                return;

            var autostartDir = Path.Combine(configDir, "autostart");
            var desktopPath = Path.Combine(autostartDir, "vaultsync.desktop");

            if (!enable)
            {
                if (File.Exists(desktopPath))
                {
                    File.Delete(desktopPath);
                }
                return;
            }

            Directory.CreateDirectory(autostartDir);
            var exe = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(exe))
                return;

            var desktop = $"[Desktop Entry]\nType=Application\nName=VaultSync\nExec=\"{exe}\"\nX-GNOME-Autostart-enabled=true\n";
            File.WriteAllText(desktopPath, desktop);
        }
    }
}
