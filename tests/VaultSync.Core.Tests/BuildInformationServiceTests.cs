using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BuildInformationServiceTests
{
    [Theory]
    [InlineData("windows-unpackaged", "github")]
    [InlineData("windows-portable", "github")]
    [InlineData("microsoft-store-msix", "microsoft-store")]
    [InlineData("macos-app", "github")]
    [InlineData("linux-appimage", "github")]
    [InlineData("development", "none")]
    public void Create_distinguishes_package_and_update_sources(string packageKind, string updateSource)
    {
        BuildInformation information = BuildInformationService.Create(
            typeof(BuildInformationServiceTests).Assembly,
            new BuildInformationOverrides(PackageKind: packageKind, UpdateSource: updateSource));

        Assert.Equal(packageKind, information.PackageKind);
        Assert.Equal(updateSource, information.UpdateSource);
        Assert.False(information.OfficialBuild);
    }

    [Fact]
    public void Create_requires_complete_stamped_metadata_before_marking_build_official()
    {
        Assembly complete = BuildAssembly(
            informationalVersion: "1.8.7+0123456789abcdef",
            ("VaultSyncReleaseChannel", "stable"),
            ("VaultSyncPackageKind", "windows-installer"),
            ("VaultSyncUpdateSource", "github"),
            ("VaultSyncOfficialBuild", "true"),
            ("VaultSyncSourceCommit", "0123456789abcdef"));
        Assembly incomplete = BuildAssembly(
            informationalVersion: "1.8.7",
            ("VaultSyncReleaseChannel", "stable"),
            ("VaultSyncPackageKind", "windows-installer"),
            ("VaultSyncOfficialBuild", "true"));

        Assert.True(BuildInformationService.Create(complete).OfficialBuild);
        Assert.False(BuildInformationService.Create(incomplete).OfficialBuild);
        Assert.Equal(BuildInformation.Unknown, BuildInformationService.Create(incomplete).SourceCommit);
    }

    [Fact]
    public void Json_uses_the_canonical_machine_readable_field_names()
    {
        BuildInformation information = BuildInformationService.Create(typeof(BuildInformationServiceTests).Assembly);
        using JsonDocument document = JsonDocument.Parse(information.ToJson());

        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("sourceCommit", out _));
        Assert.True(root.TryGetProperty("packageKind", out _));
        Assert.True(root.TryGetProperty("officialBuild", out _));
        Assert.False(root.TryGetProperty("SourceCommit", out _));
    }

    private static AssemblyBuilder BuildAssembly(string informationalVersion, params (string Key, string Value)[] metadata)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"VaultSync.BuildInformation.Tests.{Guid.NewGuid():N}") { Version = new Version(1, 8, 7) },
            AssemblyBuilderAccess.Run);
        ConstructorInfo informationalConstructor = typeof(AssemblyInformationalVersionAttribute)
            .GetConstructor([typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(informationalConstructor, [informationalVersion]));

        ConstructorInfo metadataConstructor = typeof(AssemblyMetadataAttribute)
            .GetConstructor([typeof(string), typeof(string)])!;
        foreach ((string key, string value) in metadata)
            assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataConstructor, [key, value]));

        return assembly;
    }
}
