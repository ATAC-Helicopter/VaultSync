using VaultSync.Core.Models;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupModesTests
{
    [Fact]
    public void Normalize_UnknownDefaultsToFull()
    {
        Assert.Equal(BackupModes.Full, BackupModes.Normalize(null));
        Assert.Equal(BackupModes.Full, BackupModes.Normalize(string.Empty));
        Assert.Equal(BackupModes.Full, BackupModes.Normalize("manual"));
    }

    [Fact]
    public void Normalize_IncrementalIsPreserved()
    {
        Assert.Equal(BackupModes.Incremental, BackupModes.Normalize("incremental"));
        Assert.Equal(BackupModes.Incremental, BackupModes.Normalize("InCreMental"));
    }
}

