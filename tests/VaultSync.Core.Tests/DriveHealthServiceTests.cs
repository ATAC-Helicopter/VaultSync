using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DriveHealthServiceTests
{
    [Fact]
    public void TryParseSmartCtlHealth_ParsesPassedOverallHealth()
    {
        const string output = """
smartctl 7.4 2023-08-01 r5530 [x86_64-linux-6.17] (local build)
SMART overall-health self-assessment test result: PASSED
""";

        bool parsed = DriveHealthService.TryParseSmartCtlHealth(output, out DriveHealthStatus status);

        Assert.True(parsed);
        Assert.Equal(DriveHealthStatus.Healthy, status);
    }

    [Fact]
    public void TryParseSmartCtlHealth_ParsesExplicitFailingHealth()
    {
        const string output = """
smartctl 7.4 2023-08-01 r5530 [x86_64-linux-6.17] (local build)
SMART overall-health self-assessment test result: FAILED!
""";

        bool parsed = DriveHealthService.TryParseSmartCtlHealth(output, out DriveHealthStatus status);

        Assert.True(parsed);
        Assert.Equal(DriveHealthStatus.Failing, status);
    }

    [Theory]
    [InlineData("Smartctl open device: /dev/sda failed: Permission denied")]
    [InlineData("Read Device Identity failed: scsi error unsupported scsi opcode")]
    [InlineData("Mandatory SMART command failed: exiting. To continue, add one or more '-T permissive' options.")]
    [InlineData("Unable to detect device type")]
    public void TryParseSmartCtlHealth_IgnoresUnavailableOrUnsupportedOutput(string output)
    {
        bool parsed = DriveHealthService.TryParseSmartCtlHealth(output, out DriveHealthStatus status);

        Assert.False(parsed);
        Assert.Equal(DriveHealthStatus.Unknown, status);
    }
}
