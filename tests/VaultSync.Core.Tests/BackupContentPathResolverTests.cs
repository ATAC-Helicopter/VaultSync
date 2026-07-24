using System;
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

    [Fact]
    public void Resolve_RejectsAbsoluteContentOutsideRecordedDestinations()
    {
        using var temp = new TempDirectory();
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string unrelated = Directory.CreateDirectory(Path.Combine(temp.Path, "unrelated", "point")).FullName;
        var backup = new Backup
        {
            Path = unrelated,
            DestinationPath = destination,
            DestinationAlias = "Primary"
        };
        var config = new AppConfig
        {
            Backups = new BackupsConfig
            {
                Destinations = [new BackupDestination { Path = destination, Alias = "Primary" }]
            }
        };

        Assert.Null(BackupContentPathResolver.Resolve(backup, config));
    }

    [Fact]
    public void Resolve_AllowsLegacyAbsoluteContentInsideRecordedDestination()
    {
        using var temp = new TempDirectory();
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string content = Directory.CreateDirectory(Path.Combine(destination, "App", "point")).FullName;
        var backup = new Backup { Path = content, DestinationPath = destination };
        var config = new AppConfig { Backups = new BackupsConfig { BackupRoot = destination } };

        Assert.Equal(content, BackupContentPathResolver.Resolve(backup, config));
    }

    [Fact]
    public void Resolve_RejectsLinkedContentBelowRecordedDestination()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TempDirectory();
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string unrelated = Directory.CreateDirectory(Path.Combine(temp.Path, "unrelated", "point")).FullName;
        string linked = Path.Combine(destination, "linked");
        Directory.CreateSymbolicLink(linked, unrelated);
        var backup = new Backup
        {
            Path = linked,
            DestinationPath = destination,
            DestinationAlias = "Primary"
        };
        var config = new AppConfig
        {
            Backups = new BackupsConfig
            {
                Destinations = [new BackupDestination { Path = destination, Alias = "Primary" }]
            }
        };

        Assert.Null(BackupContentPathResolver.Resolve(backup, config));
    }
}
