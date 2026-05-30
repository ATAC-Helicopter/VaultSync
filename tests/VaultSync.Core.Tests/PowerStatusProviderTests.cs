using System.IO;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PowerStatusProviderTests
{
    [Fact]
    public void GetLinuxState_IgnoresDeviceScopedControllerBattery()
    {
        using var root = new TempDirectory();
        string battery = Directory.CreateDirectory(Path.Combine(root.Path, "ps-controller-battery")).FullName;
        File.WriteAllText(Path.Combine(battery, "type"), "Battery");
        File.WriteAllText(Path.Combine(battery, "scope"), "Device");
        File.WriteAllText(Path.Combine(battery, "status"), "Discharging");

        PowerState state = PowerStatusProvider.GetLinuxState(root.Path);

        Assert.Equal(PowerState.Unknown, state);
    }

    [Fact]
    public void GetLinuxState_SystemBatteryDischarging_ReturnsOnBattery()
    {
        using var root = new TempDirectory();
        string battery = Directory.CreateDirectory(Path.Combine(root.Path, "BAT0")).FullName;
        File.WriteAllText(Path.Combine(battery, "type"), "Battery");
        File.WriteAllText(Path.Combine(battery, "scope"), "System");
        File.WriteAllText(Path.Combine(battery, "status"), "Discharging");

        PowerState state = PowerStatusProvider.GetLinuxState(root.Path);

        Assert.Equal(PowerState.OnBattery, state);
    }

    [Fact]
    public void GetLinuxState_AcOnline_ReturnsPluggedInEvenWithBattery()
    {
        using var root = new TempDirectory();
        string ac = Directory.CreateDirectory(Path.Combine(root.Path, "AC")).FullName;
        File.WriteAllText(Path.Combine(ac, "type"), "Mains");
        File.WriteAllText(Path.Combine(ac, "online"), "1");

        string battery = Directory.CreateDirectory(Path.Combine(root.Path, "BAT0")).FullName;
        File.WriteAllText(Path.Combine(battery, "type"), "Battery");
        File.WriteAllText(Path.Combine(battery, "status"), "Discharging");

        PowerState state = PowerStatusProvider.GetLinuxState(root.Path);

        Assert.Equal(PowerState.PluggedIn, state);
    }
}
