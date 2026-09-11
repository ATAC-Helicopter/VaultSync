using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
                    return GetLinuxState("/sys/class/power_supply");
            }
            catch
            {
                // Ignore and fall through to Unknown.
            }

            return PowerState.Unknown;
        }

        private static PowerState GetWindowsState()
        {
            if (!GetSystemPowerStatus(out SystemPowerStatus status))
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

        internal static PowerState GetLinuxState(string root)
        {
            try
            {
                if (!Directory.Exists(root))
                    return PowerState.Unknown;

                LinuxSupplyState[] supplies = Directory.GetDirectories(root)
                    .Select(ReadLinuxSupplyState)
                    .ToArray();
                if (supplies.Contains(LinuxSupplyState.AcOnline))
                    return PowerState.PluggedIn;
                if (supplies.Contains(LinuxSupplyState.BatteryDischarging))
                    return PowerState.OnBattery;
            }
            catch
            {
                // ignore
            }

            return PowerState.Unknown;
        }

        private static LinuxSupplyState ReadLinuxSupplyState(string directory)
        {
            string? type = TryReadPowerSupplyValue(directory, "type");
            if (string.Equals(type, "Mains", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "AC", StringComparison.OrdinalIgnoreCase))
            {
                return TryReadPowerSupplyValue(directory, "online") == "1"
                    ? LinuxSupplyState.AcOnline
                    : LinuxSupplyState.Unknown;
            }

            if (!string.Equals(type, "Battery", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(TryReadPowerSupplyValue(directory, "scope"), "Device", StringComparison.OrdinalIgnoreCase))
                return LinuxSupplyState.Unknown;

            return string.Equals(
                TryReadPowerSupplyValue(directory, "status"),
                "Discharging",
                StringComparison.OrdinalIgnoreCase)
                ? LinuxSupplyState.BatteryDischarging
                : LinuxSupplyState.Unknown;
        }

        private static string? TryReadPowerSupplyValue(string directory, string name)
        {
            string path = Path.Combine(directory, name);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        private enum LinuxSupplyState
        {
            Unknown,
            AcOnline,
            BatteryDischarging
        }

        // Windows API
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
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
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);
    }
}
