using VaultSync.Core.Config;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RuntimeLogTests
{
    [Fact]
    public void UpdateFromConfig_DisablesVerboseWhenFlagsAreOff()
    {
        RuntimeLog.UpdateFromConfig(new AppConfig
        {
            Advanced = new AdvancedConfig
            {
                VerboseLogging = false,
                SaveVerboseLogs = false
            }
        });

        Assert.False(RuntimeLog.ShouldEmitVerbose);
    }

    [Fact]
    public void UpdateFromConfig_EnablesVerboseWhenVerboseLoggingIsOn()
    {
        RuntimeLog.UpdateFromConfig(new AppConfig
        {
            Advanced = new AdvancedConfig
            {
                VerboseLogging = true,
                SaveVerboseLogs = false
            }
        });

        Assert.True(RuntimeLog.ShouldEmitVerbose);
    }

    [Fact]
    public void UpdateFromConfig_EnablesVerboseWhenDiskLoggingIsOn()
    {
        RuntimeLog.UpdateFromConfig(new AppConfig
        {
            Advanced = new AdvancedConfig
            {
                VerboseLogging = false,
                SaveVerboseLogs = true
            }
        });

        Assert.True(RuntimeLog.ShouldEmitVerbose);
    }
}
