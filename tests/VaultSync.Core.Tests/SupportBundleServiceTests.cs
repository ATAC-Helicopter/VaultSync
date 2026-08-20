using System;
using System.Linq;
using System.Text.Json;
using VaultSync.Core.Config;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SupportBundleServiceTests
{
    [Fact]
    public void RedactedConfig_DoesNotExposePathsCredentialsOrPlaintextPasswords()
    {
        const string sourcePath = "/Users/alice/Projects/SecretProject";
        const string destinationPath = "smb://alice:network-password@example.invalid/PrivateShare";
        var config = new AppConfig
        {
            ProjectsRoot = sourcePath,
            DbPath = "/Users/alice/Library/Application Support/VaultSync/vaultsync.db"
        };
        config.Backups.BackupRoot = destinationPath;
        config.Backups.Destinations.Add(new BackupDestination
        {
            Alias = "Private NAS",
            Path = destinationPath,
            CredentialName = "Alice NAS"
        });
        config.Network.Credentials.Add(new NetworkCredentialProfile
        {
            Name = "Alice NAS",
            Username = "alice",
            Domain = "private-domain",
            KeyRef = "cred-alice-private",
            Password = "network-password"
        });

        string json = JsonSerializer.Serialize(SupportBundleService.BuildRedactedConfig(config));

        Assert.DoesNotContain("alice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("network-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretProject", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateShare", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Private NAS", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-domain", json, StringComparison.Ordinal);
        Assert.Contains("path-", json, StringComparison.Ordinal);
        Assert.Contains("[redacted]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeText_RemovesKnownAndStructuredSecretForms()
    {
        var config = new AppConfig { ProjectsRoot = "/Users/alice/Projects/SecretProject" };
        config.Network.Credentials.Add(new NetworkCredentialProfile
        {
            Username = "alice",
            KeyRef = "cred-alice-1234",
            Password = "network-password"
        });
        const string input = """
            smb://alice:network-password@example.invalid/share
            {"password":"network-password","token":"api-token","keyRef":"cred-alice-1234"}
            /Users/alice/Projects/SecretProject
            """;

        string sanitized = SupportBundleService.SanitizeText(input, config);

        Assert.DoesNotContain("alice", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("network-password", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("api-token", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("cred-alice-1234", sanitized, StringComparison.Ordinal);
        Assert.Contains("[redacted]", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeText_RemovesConfiguredAliasesAndCredentialProfileNames()
    {
        var config = new AppConfig();
        config.Backups.Destinations.Add(new BackupDestination { Alias = "Family archive" });
        config.Network.Credentials.Add(new NetworkCredentialProfile { Name = "Home NAS login" });

        string sanitized = SupportBundleService.SanitizeText(
            "Destination Family archive uses Home NAS login.",
            config);

        Assert.DoesNotContain("Family archive", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Home NAS login", sanitized, StringComparison.Ordinal);
        Assert.Equal(2, sanitized.Split("[redacted]", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Preview_AllowsOptionalDiagnosticSectionsToBeRemoved()
    {
        SupportBundlePreviewResult preview = SupportBundleService.Preview(
            new SupportBundleExportOptions(
                IncludeDiagnostics: false,
                IncludeTelemetry: false));

        Assert.True(preview.Success);
        Assert.Equal(2, preview.Files.Count);
        Assert.All(preview.Files, item => Assert.True(item.Required));
        Assert.Equal(
            ["support-manifest.json", "support-report.json"],
            preview.Files.Select(item => item.RelativePath).OrderBy(path => path).ToArray());
    }

    [Fact]
    public void PathPseudonyms_AreStableWithoutRevealingTheLeafName()
    {
        string first = SupportBundleService.RedactPath("/Users/alice/Projects/SecretProject");
        string second = SupportBundleService.RedactPath("/Users/alice/Projects/SecretProject");

        Assert.Equal(first, second);
        Assert.StartsWith("path-", first, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretProject", first, StringComparison.Ordinal);
    }
}
