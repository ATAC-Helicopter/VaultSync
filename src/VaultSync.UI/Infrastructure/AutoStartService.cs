using System;
using System.IO;
using Microsoft.Win32;

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
                Console.WriteLine($"[AutoStart] Failed to set launch on login: {ex.Message}");
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
                    File.Delete(plistPath);
                }
                return;
            }

            Directory.CreateDirectory(agentDir);
            var exe = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(exe))
                return;

            var plist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
  <dict>
    <key>Label</key><string>com.vaultsync.autostart</string>
    <key>ProgramArguments</key>
    <array>
      <string>{exe}</string>
    </array>
    <key>RunAtLoad</key><true/>
  </dict>
</plist>";
            File.WriteAllText(plistPath, plist);
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
