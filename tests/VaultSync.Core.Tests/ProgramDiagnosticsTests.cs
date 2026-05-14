using System;
using VaultSync.UI;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProgramDiagnosticsTests
{
    [Fact]
    public void IsFirstChanceDiagnosticsEnabled_DefaultsOff()
    {
        string previous = Environment.GetEnvironmentVariable("VAULTSYNC_FIRST_CHANCE_DIAGNOSTICS");
        try
        {
            Environment.SetEnvironmentVariable("VAULTSYNC_FIRST_CHANCE_DIAGNOSTICS", null);

            Assert.False(Program.IsFirstChanceDiagnosticsEnabled([]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAULTSYNC_FIRST_CHANCE_DIAGNOSTICS", previous);
        }
    }

    [Fact]
    public void IsFirstChanceDiagnosticsEnabled_AllowsExplicitOptIn()
    {
        Assert.True(Program.IsFirstChanceDiagnosticsEnabled(["--diagnostic-first-chance"]));
    }
}
