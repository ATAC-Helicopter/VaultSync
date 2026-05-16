using System;
using System.IO;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PowerStatusProviderTests
{
    [Fact]
    public void GetLinuxState_IgnoresDeviceScopedControllerBattery()
    {
        string root = CreateTempPowerSupplyRoot();
        try
        {
            string battery = Directory.CreateDirectory(Path.Combine(root, "ps-controller-battery")).FullName;
            File.WriteAllText(Path.Combine(battery, "type"), "Battery");
            File.WriteAllText(Path.Combine(battery, "scope"), "Device");
            File.WriteAllText(Path.Combine(battery, "status"), "Discharging");

            PowerState state = PowerStatusProvider.GetLinuxState(root);

            Assert.Equal(PowerState.Unknown, state);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetLinuxState_SystemBatteryDischarging_ReturnsOnBattery()
    {
        string root = CreateTempPowerSupplyRoot();
        try
        {
            string battery = Directory.CreateDirectory(Path.Combine(root, "BAT0")).FullName;
            File.WriteAllText(Path.Combine(battery, "type"), "Battery");
            File.WriteAllText(Path.Combine(battery, "scope"), "System");
            File.WriteAllText(Path.Combine(battery, "status"), "Discharging");

            PowerState state = PowerStatusProvider.GetLinuxState(root);

            Assert.Equal(PowerState.OnBattery, state);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetLinuxState_AcOnline_ReturnsPluggedInEvenWithBattery()
    {
        string root = CreateTempPowerSupplyRoot();
        try
        {
            string ac = Directory.CreateDirectory(Path.Combine(root, "AC")).FullName;
            File.WriteAllText(Path.Combine(ac, "type"), "Mains");
            File.WriteAllText(Path.Combine(ac, "online"), "1");

            string battery = Directory.CreateDirectory(Path.Combine(root, "BAT0")).FullName;
            File.WriteAllText(Path.Combine(battery, "type"), "Battery");
            File.WriteAllText(Path.Combine(battery, "status"), "Discharging");

            PowerState state = PowerStatusProvider.GetLinuxState(root);

            Assert.Equal(PowerState.PluggedIn, state);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempPowerSupplyRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vaultsync-power-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
