using System.IO;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupContentPathResolverTests
{
    [Fact]
    public void Resolve_DoesNotBorrowSameNamedContentFromAnUnrelatedDestination()
    {
        using var temp = new TempDirectory();
        string missingRoot = Path.Combine(temp.Path, "missing-destination");
        string otherRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "other-destination")).FullName;
        string relativePath = Path.Combine("App", "point");
        Directory.CreateDirectory(Path.Combine(otherRoot, relativePath));
        var backup = new Backup
        {
            Path = relativePath,
            DestinationPath = missingRoot,
            DestinationAlias = "Missing"
        };
        var config = new AppConfig
        {
            Backups = new BackupsConfig
            {
                Destinations =
                [
                    new BackupDestination { Path = missingRoot, Alias = "Missing" },
                    new BackupDestination { Path = otherRoot, Alias = "Other" }
                ]
            }
        };

        Assert.Null(BackupContentPathResolver.Resolve(backup, config));
    }

    [Fact]
    public void Resolve_DoesNotBorrowSameNamedContentFromLegacyBackupRoot()
    {
        using var temp = new TempDirectory();
        string missingRoot = Path.Combine(temp.Path, "missing-destination");
        string legacyRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "legacy-root")).FullName;
        string relativePath = Path.Combine("App", "point");
        Directory.CreateDirectory(Path.Combine(legacyRoot, relativePath));
        var backup = new Backup
        {
            Path = relativePath,
            DestinationPath = missingRoot,
            DestinationAlias = "Missing"
        };
        var config = new AppConfig
        {
            Backups = new BackupsConfig { BackupRoot = legacyRoot }
        };

        Assert.Null(BackupContentPathResolver.Resolve(backup, config));
    }

    [Fact]
    public void Resolve_FollowsAnExplicitAliasWhenDestinationPathMoved()
    {
        using var temp = new TempDirectory();
        string movedRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "moved-destination")).FullName;
        string relativePath = Path.Combine("App", "point");
        string expected = Directory.CreateDirectory(Path.Combine(movedRoot, relativePath)).FullName;
        var backup = new Backup
        {
            Path = relativePath,
            DestinationPath = Path.Combine(temp.Path, "old-destination"),
            DestinationAlias = "Archive"
        };
        var config = new AppConfig
        {
            Backups = new BackupsConfig
            {
                Destinations = [new BackupDestination { Path = movedRoot, Alias = "Archive" }]
            }
        };

        Assert.Equal(expected, BackupContentPathResolver.Resolve(backup, config));
    }
}
