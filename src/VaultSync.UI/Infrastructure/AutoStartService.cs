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
                DiagnosticsLogger.RecordException("Launch-on-login update failed", ex);
            }
        }

        private static string GetExecutablePath()
        {
            string? path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            }
            return path;
        }

        private static (string FileName, string[] Args) GetLaunchCommand()
        {
            string exe = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(exe))
                return (string.Empty, Array.Empty<string>());

            string baseDir = AppContext.BaseDirectory;
            string dllPath = Path.Combine(baseDir, "VaultSync.UI.dll");

            if (exe.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) && File.Exists(dllPath))
            {
                return (exe, [dllPath]);
            }

            if (File.Exists(exe))
            {
                return (exe, Array.Empty<string>());
            }

            if (File.Exists(dllPath))
            {
                return ("dotnet", new[] { dllPath });
            }

            return (string.Empty, Array.Empty<string>());
        }

        [SupportedOSPlatform("windows")]
        private static void SetWindowsAutoStart(bool enable)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: true)
                           ?? Registry.CurrentUser.CreateSubKey(WindowsRunKey);
            if (key is null)
                return;

            if (enable)
            {
                (string fileName, string[] args) = GetLaunchCommand();
                if (string.IsNullOrWhiteSpace(fileName))
                    return;

                string command = BuildCommandLine(fileName, args);
                key.SetValue(AppId, command);
            }
            else
            {
                if (key.GetValue(AppId) != null)
                    key.DeleteValue(AppId, throwOnMissingValue: false);
            }
        }

        private static void SetMacAutoStart(bool enable)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            string agentDir = Path.Combine(home, "Library", "LaunchAgents");
            string plistPath = Path.Combine(agentDir, "com.vaultsync.autostart.plist");
            string? uid = GetMacUid();

            if (!enable)
            {
                if (File.Exists(plistPath))
                {
                    if (!string.IsNullOrWhiteSpace(uid))
                    {
                        TryLaunchCtl($"bootout gui/{uid} \"{plistPath}\"");
                        TryLaunchCtl($"disable gui/{uid}/com.vaultsync.autostart");
                    }
                    File.Delete(plistPath);
                }
                return;
            }

            Directory.CreateDirectory(agentDir);
            (string fileName, string[] launchArgs) = GetLaunchCommand();
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            string[] args = [fileName, .. launchArgs];
            string programArgs = string.Join(
                Environment.NewLine,
                args.Select(arg => $"      <string>{XmlEscape(arg)}</string>"));
            string plist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
  <dict>
    <key>Label</key><string>com.vaultsync.autostart</string>
    <key>ProgramArguments</key>
    <array>
{programArgs}
    </array>
    <key>WorkingDirectory</key><string>{XmlEscape(AppContext.BaseDirectory)}</string>
    <key>RunAtLoad</key><true/>
  </dict>
</plist>";
            File.WriteAllText(plistPath, plist);
            if (string.IsNullOrWhiteSpace(uid))
                return;

            // Ensure any previous instance is unloaded before reloading the updated plist.
            TryLaunchCtl($"bootout gui/{uid} \"{plistPath}\"");
            TryLaunchCtl($"bootstrap gui/{uid} \"{plistPath}\"");
            TryLaunchCtl($"enable gui/{uid}/com.vaultsync.autostart");
            TryLaunchCtl($"kickstart -k gui/{uid}/com.vaultsync.autostart");
        }

        private static string? GetMacUid()
        {
            try
            {
                string? uid = Environment.GetEnvironmentVariable("UID");
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
                    return null;

                return uid;
            }
            catch
            {
                // Swallow errors; LaunchAgent will still load on next login.
                return null;
            }
        }

        private static void TryLaunchCtl(string arguments)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "launchctl",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                proc?.WaitForExit(3000);
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
            string configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(configDir))
                return;

            string autostartDir = Path.Combine(configDir, "autostart");
            string desktopPath = Path.Combine(autostartDir, "vaultsync.desktop");

            if (!enable)
            {
                if (File.Exists(desktopPath))
                {
                    File.Delete(desktopPath);
                }
                return;
            }

            Directory.CreateDirectory(autostartDir);
            (string fileName, string[] args) = GetLaunchCommand();
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            string execLine = BuildCommandLine(fileName, args);
            string desktop = $"[Desktop Entry]\nType=Application\nName=VaultSync\nExec={execLine}\nX-GNOME-Autostart-enabled=true\n";
            File.WriteAllText(desktopPath, desktop);
        }

        private static string BuildCommandLine(string fileName, string[] args)
        {
            System.Collections.Generic.IEnumerable<string> parts = [fileName, .. args];
            return string.Join(" ", parts.Select(QuoteArgument));
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "\"\"";

            if (!value.Any(char.IsWhiteSpace) && !value.Contains('"'))
                return value;

            return $"\"{value.Replace("\"", "\\\"")}\"";
        }
    }
}
