using System;
using System.IO;
using VaultSync.CLI.Config;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliPathExpansionTests
{
    [Theory]
    [InlineData("folder~copy/database.db")]
    [InlineData("~another-user/database.db")]
    public void ExpandUserPathDoesNotReplaceLiteralTildes(string path)
    {
        Assert.Equal(path, ConfigHelper.ExpandUserPath(path));
    }

    [Theory]
    [InlineData("~/vaultsync/database.db")]
    [InlineData("~\\vaultsync\\database.db")]
    public void ExpandUserPathExpandsLeadingHomeSegment(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string expected = Path.Combine(home, "vaultsync", "database.db");

        Assert.Equal(expected, ConfigHelper.ExpandUserPath(path));
    }
}
