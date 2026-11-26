using System;
using System.IO;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskRecoveryRestoreRequestTests
{
    [Fact]
    public void Requests_replace_previous_selection_without_touching_images()
    {
        using var temporary = new TempDirectory();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        string firstFolder = Directory.CreateDirectory(Path.Combine(temporary.Path, first.ToString("N"))).FullName;
        string secondFolder = Directory.CreateDirectory(Path.Combine(temporary.Path, second.ToString("N"))).FullName;
        string payload = Path.Combine(firstFolder, "payload.raw");
        File.WriteAllText(payload, "fixture");

        Assert.Throws<ArgumentException>(() => DiskRecoveryRestoreRequest.Save(firstFolder, second));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path));
        DiskRecoveryRestoreRequest.Save(firstFolder, first);
        DiskRecoveryRestoreRequest.Save(secondFolder + Path.DirectorySeparatorChar, second);

        string marker = Assert.Single(Directory.GetFiles(temporary.Path));
        Assert.Equal(DiskRecoveryRestoreRequest.FileName, Path.GetFileName(marker));
        Assert.Equal(second.ToString("N"), File.ReadAllText(marker));
        Assert.Equal("fixture", File.ReadAllText(payload));
    }
}
