using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VaultSync.UI.Services
{
    public enum PowerState
    {
        Unknown,
        OnBattery,
        PluggedIn
    }

    public interface IPowerStatusProvider
    {
        PowerState GetPowerState();
    }

    /// <summary>
    /// Minimal cross-platform power status helper.
    /// - Windows: kernel32 GetSystemPowerStatus.
    /// - macOS: pmset -g batt.
    /// - Linux: /sys/class/power_supply.
    /// Falls back to Unknown on errors.
    /// </summary>
    public sealed class PowerStatusProvider : IPowerStatusProvider
    {
        public PowerState GetPowerState()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return GetWindowsState();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return GetMacState();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return GetLinuxState();
            }
            catch
            {
                // Ignore and fall through to Unknown.
            }

            return PowerState.Unknown;
        }

        private static PowerState GetWindowsState()
        {
            if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
                return PowerState.Unknown;

            return status.ACLineStatus switch
            {
                0 => PowerState.OnBattery,
                1 => PowerState.PluggedIn,
                _ => PowerState.Unknown
            };
        }

        private static PowerState GetMacState()
        {
            // pmset -g batt outputs lines containing "AC Power" or "Battery Power"
            const string pmsetPath = "/usr/bin/pmset";
            if (!File.Exists(pmsetPath))
                return PowerState.Unknown;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pmsetPath,
                    Arguments = "-g batt",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc is null)
                    return PowerState.Unknown;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2000);

                if (output.IndexOf("AC Power", StringComparison.OrdinalIgnoreCase) >= 0)
                    return PowerState.PluggedIn;

                if (output.IndexOf("Battery Power", StringComparison.OrdinalIgnoreCase) >= 0)
                    return PowerState.OnBattery;
            }
            catch
            {
                // ignore
            }

            return PowerState.Unknown;
        }

        private static PowerState GetLinuxState()
        {
            const string root = "/sys/class/power_supply";
            try
            {
                if (!Directory.Exists(root))
                    return PowerState.Unknown;

                string[] entries = Directory.GetDirectories(root);
                bool anyAcOnline = false;
                bool anyBatteryDischarging = false;

                foreach (string dir in entries)
                {
                    string typePath = Path.Combine(dir, "type");
                    if (!File.Exists(typePath))
                        continue;

                    string type = File.ReadAllText(typePath).Trim();
                    if (string.Equals(type, "Mains", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type, "AC", StringComparison.OrdinalIgnoreCase))
                    {
                        string onlinePath = Path.Combine(dir, "online");
                        if (File.Exists(onlinePath))
                        {
                            string online = File.ReadAllText(onlinePath).Trim();
                            if (online == "1")
                                anyAcOnline = true;
                        }
                        continue;
                    }

                    if (string.Equals(type, "Battery", StringComparison.OrdinalIgnoreCase))
                    {
                        string statusPath = Path.Combine(dir, "status");
                        if (File.Exists(statusPath))
                        {
                            string status = File.ReadAllText(statusPath).Trim();
                            if (status.Equals("Discharging", StringComparison.OrdinalIgnoreCase))
                            {
                                anyBatteryDischarging = true;
                            }
                        }
                    }
                }

                if (anyAcOnline)
                    return PowerState.PluggedIn;

                if (anyBatteryDischarging)
                    return PowerState.OnBattery;
            }
            catch
            {
                // ignore
            }

            return PowerState.Unknown;
        }

        // Windows API
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
    }
}
