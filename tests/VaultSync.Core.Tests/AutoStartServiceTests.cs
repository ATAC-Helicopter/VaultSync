using System.IO;
using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class AutoStartServiceTests
{
    [Fact]
    public void ResolveMacUserHome_PrefersUserProfileOverDocumentsAndEnvironmentFallbacks()
    {
        string expected = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "Users", "test"));

        string actual = AutoStartService.ResolveMacUserHome(
            expected,
            Path.Combine(expected, "fallback"));

        Assert.Equal(expected, actual);
        Assert.Equal(
            Path.Combine(expected, "Library", "LaunchAgents"),
            Path.Combine(actual, "Library", "LaunchAgents"));
    }

    [Fact]
    public void ResolveMacUserHome_UsesHomeEnvironmentWhenUserProfileIsUnavailable()
    {
        string expected = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "Users", "fallback"));

        string actual = AutoStartService.ResolveMacUserHome(null, expected);

        Assert.Equal(expected, actual);
    }
}
