using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class InstallationIdentityServiceTests
{
    [Fact]
    public void Constructor_WithoutOverrideUsesVaultSyncApplicationDataDirectory()
    {
        var service = new InstallationIdentityService();

        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VaultSync",
                InstallationIdentityService.IdentityFileName),
            service.IdentityPath);
    }

    [Fact]
    public void GetOrCreate_CreatesCanonicalDurableIdentity()
    {
        using var directory = new TempDirectory();
        var firstService = new InstallationIdentityService(directory.Path);

        string first = firstService.GetOrCreate();
        string second = new InstallationIdentityService(directory.Path).GetOrCreate();

        Assert.Equal(32, first.Length);
        Assert.Equal(first, first.ToLowerInvariant());
        Assert.True(Guid.TryParseExact(first, "N", out Guid parsed));
        Assert.NotEqual(Guid.Empty, parsed);
        Assert.Equal(first, second);
        Assert.Equal(first, File.ReadAllText(firstService.IdentityPath).Trim());
    }

    [Fact]
    public async System.Threading.Tasks.Task GetOrCreate_ConcurrentCallersObserveOneIdentity()
    {
        using var directory = new TempDirectory();
        var service = new InstallationIdentityService(directory.Path);

        string[] identities = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(_ => Task.Run(service.GetOrCreate)));

        Assert.Single(identities.Distinct(StringComparer.Ordinal));
        Assert.Equal(identities[0], File.ReadAllText(service.IdentityPath).Trim());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-identity")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFAB")]
    public void GetOrCreate_MalformedExistingIdentityFailsClosed(string value)
    {
        using var directory = new TempDirectory();
        var service = new InstallationIdentityService(directory.Path);
        File.WriteAllText(service.IdentityPath, value);

        InvalidDataException error = Assert.Throws<InvalidDataException>(service.GetOrCreate);

        Assert.Contains("was not replaced", error.Message, StringComparison.Ordinal);
        Assert.Equal(value, File.ReadAllText(service.IdentityPath));
    }

    [Fact]
    public void GetOrCreate_RestrictsUnixIdentityPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var directory = new TempDirectory();
        var service = new InstallationIdentityService(directory.Path);

        service.GetOrCreate();

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(service.IdentityPath));
    }

    [Fact]
    public void GetOrCreate_RejectsSymbolicLinkIdentity()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var directory = new TempDirectory();
        string targetPath = Path.Combine(directory.Path, "identity-target.txt");
        File.WriteAllText(targetPath, Guid.NewGuid().ToString("N"));
        var service = new InstallationIdentityService(directory.Path);
        File.CreateSymbolicLink(service.IdentityPath, targetPath);

        Assert.Throws<InvalidDataException>(service.GetOrCreate);
    }
}
