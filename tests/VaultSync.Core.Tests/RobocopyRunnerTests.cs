using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RobocopyRunnerTests
{
    [Theory]
    [InlineData("", false, false, "")]
    [InlineData("  # generated output", false, false, "")]
    [InlineData("**/bin/**", true, true, "bin")]
    [InlineData("**\\obj\\**", true, true, "obj")]
    [InlineData("cache/", true, true, "cache")]
    [InlineData("./*.tmp", true, false, "*.tmp")]
    [InlineData("generated/**/*.js", true, false, "generated\\*\\*.js")]
    [InlineData("./", false, false, "")]
    public void TryNormalizeIgnorePattern_ClassifiesRobocopyExclusions(
        string raw,
        bool expectedResult,
        bool expectedDirectory,
        string expectedPattern)
    {
        bool result = RobocopyRunner.TryNormalizeIgnorePattern(raw, out string pattern, out bool isDirectory);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedDirectory, isDirectory);
        Assert.Equal(expectedPattern, pattern);
    }
}
