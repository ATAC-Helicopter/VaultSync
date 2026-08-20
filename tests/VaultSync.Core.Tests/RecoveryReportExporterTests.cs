#nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.Core.Services;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryReportExporterTests
{
    [Fact]
    public void BuildMarkdown_IncludesOverviewCoverageRecommendationAndProjects()
    {
        RecoveryReportSnapshot snapshot = CreateSnapshot();

        string report = RecoveryReportExporter.BuildMarkdown(snapshot, CreateLabels());

        Assert.Contains("# VaultSync Recovery Report", report);
        Assert.Contains("**Readiness:** 78% (Review)", report);
        Assert.Contains("**24h:** 1/2 (50%)", report);
        Assert.Contains("Protect a known-good restore point.", report);
        Assert.Contains("| Project Alpha | Review | 72% |", report);
        Assert.DoesNotContain("password", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keyRef", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMarkdown_EscapesProjectTableContent()
    {
        RecoveryReportSnapshot snapshot = CreateSnapshot() with
        {
            Projects =
            [
                new RecoveryReportProject("Project | Alpha", "Review", 72, "Line one\nLine | two")
            ]
        };

        string report = RecoveryReportExporter.BuildMarkdown(snapshot, CreateLabels());

        Assert.Contains("Project \\| Alpha", report);
        Assert.Contains("Line one Line \\| two", report);
    }

    [Fact]
    public void ExportMarkdown_WritesUniquePortableReport()
    {
        using var temp = new TempDirectory();
        RecoveryReportSnapshot snapshot = CreateSnapshot();

        string first = RecoveryReportExporter.ExportMarkdown(snapshot, CreateLabels(), temp.Path);
        string second = RecoveryReportExporter.ExportMarkdown(snapshot, CreateLabels(), temp.Path);

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.NotEqual(first, second);
        Assert.EndsWith(".md", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMarkdown_WithNoProjects_UsesEmptyMessage()
    {
        RecoveryReportSnapshot snapshot = CreateSnapshot() with
        {
            ProjectCount = 0,
            Projects = []
        };

        string report = RecoveryReportExporter.BuildMarkdown(snapshot, CreateLabels());

        Assert.Contains("No projects measured.", report);
    }

    [Fact]
    public void BuildMarkdown_IncludesStableRecoveryProofEvidence()
    {
        RecoveryReportSnapshot snapshot = CreateSnapshot() with
        {
            Projects =
            [
                new RecoveryReportProject(
                    "Project Alpha",
                    "Review",
                    72,
                    "Proof needs review.",
                    Evidence:
                    [
                        new RecoveryReportEvidence(
                            "hash_mismatch",
                            "Failed",
                            "Stored SHA-256 does not match.",
                            "hash_mismatch:src/app.txt",
                            "src/app.txt")
                    ])
            ]
        };

        string report = RecoveryReportExporter.BuildMarkdown(snapshot, CreateLabels());

        Assert.Contains("## Recovery proof evidence", report);
        Assert.Contains("hash_mismatch:src/app.txt", report);
        Assert.Contains("src/app.txt", report);
        Assert.Contains("isolated test folder", report);
        Assert.Contains("## Report identity", report);
        Assert.Contains("SHA-256", report);
    }

    [Fact]
    public void BuildMarkdown_IncludesCanonicalBuildIdentity()
    {
        BuildInformation build = new(
            1, "VaultSync", "1.8.7", "stable", "abcdef123456", ".NET 10", "win-x64",
            "x64", "Windows", "windows-installer", "github", true, "unsigned");
        RecoveryReportSnapshot snapshot = CreateSnapshot() with { AppVersion = build.Version, Build = build };

        string report = RecoveryReportExporter.BuildMarkdown(snapshot, CreateLabels());

        Assert.Contains("**Source commit:** abcdef123456", report);
        Assert.Contains("**Package:** windows-installer; updates: github", report);
        Assert.Contains("**Official build:** yes", report);
        Assert.Contains("**Signature:** unsigned", report);
    }

    [Fact]
    public void ExportEvidencePackage_IsPortableChecksummedAndDeterministic()
    {
        using var temp = new TempDirectory();
        RecoveryReportSnapshot snapshot = CreateSnapshot() with
        {
            Projects =
            [
                new RecoveryReportProject(
                    "Project Alpha", "Review", 72, "Proof needs review.",
                    Evidence:
                    [
                        new RecoveryReportEvidence(
                            "integrity", "Passed", "Content verified.", "proof-1",
                            Path.Combine(temp.Path, "private", "result.json"))
                    ],
                    RepositoryIdentity: "repository-abc123",
                    ConfidenceEvidence:
                    [
                        new RecoveryReportConfidenceEvidence(
                            "Credential", "Measured", "Passed", "credential.available", null),
                        new RecoveryReportConfidenceEvidence(
                            "IntegrityVerification", "Measured", "Stale", "verification.stale",
                            new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero))
                    ])
            ]
        };

        string first = RecoveryEvidencePackage.Export(snapshot, CreateLabels(), temp.Path);
        string second = RecoveryEvidencePackage.Export(snapshot, CreateLabels(), temp.Path);

        Assert.EndsWith(".zip", first, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(first, second);
        Assert.True(RecoveryEvidencePackage.Validate(first).IsValid);
        Assert.True(RecoveryEvidencePackage.Validate(second).IsValid);
        using ZipArchive firstArchive = ZipFile.OpenRead(first);
        using ZipArchive secondArchive = ZipFile.OpenRead(second);
        string[] expected = ["manifest.json", "recovery-evidence.json", "recovery-report.md", "SHA256SUMS"];
        Assert.Equal(expected.Order(), firstArchive.Entries.Select(entry => entry.FullName).Order());
        Assert.Equal(
            ReadEntry(firstArchive, "SHA256SUMS"),
            ReadEntry(secondArchive, "SHA256SUMS"));
        string evidence = ReadEntry(firstArchive, "recovery-evidence.json");
        Assert.Contains("repository-abc123", evidence);
        Assert.Contains("\"hasEncryptedRecoveryPointEvidence\": true", evidence);
        Assert.Contains("\"code\": \"verification.stale\"", evidence);
        Assert.Contains("\"observedAtUtc\": \"2026-06-01T12:00:00+00:00\"", evidence);
        Assert.Contains("[local path redacted]/result.json", evidence);
        Assert.DoesNotContain(temp.Path, evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateEvidencePackage_RejectsTamperingAndUnexpectedEntries()
    {
        using var temp = new TempDirectory();
        string package = RecoveryEvidencePackage.Export(CreateSnapshot(), CreateLabels(), temp.Path);
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            ZipArchiveEntry report = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("recovery-report.md"));
            report.Delete();
            ZipArchiveEntry replacement = archive.CreateEntry("recovery-report.md");
            using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
            writer.Write("altered");
        }

        RecoveryEvidencePackageValidationResult result = RecoveryEvidencePackage.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains("Checksum validation failed", result.Message);
    }

    [Theory]
    [InlineData("../unsafe.json")]
    [InlineData("extra.json")]
    public void ValidateEvidencePackage_RejectsUnsafeOrUnexpectedEntry(string entryName)
    {
        using var temp = new TempDirectory();
        string package = RecoveryEvidencePackage.Export(CreateSnapshot(), CreateLabels(), temp.Path);
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Update))
            archive.CreateEntry(entryName);

        Assert.False(RecoveryEvidencePackage.Validate(package).IsValid);
    }

    [Fact]
    public void ValidateEvidencePackage_RejectsMissingAndDuplicateEntries()
    {
        using var temp = new TempDirectory();
        string missing = RecoveryEvidencePackage.Export(CreateSnapshot(), CreateLabels(), temp.Path);
        using (ZipArchive archive = ZipFile.Open(missing, ZipArchiveMode.Update))
            Assert.IsType<ZipArchiveEntry>(archive.GetEntry("manifest.json")).Delete();
        Assert.False(RecoveryEvidencePackage.Validate(missing).IsValid);

        string duplicate = RecoveryEvidencePackage.Export(CreateSnapshot(), CreateLabels(), temp.Path);
        using (ZipArchive archive = ZipFile.Open(duplicate, ZipArchiveMode.Update))
            archive.CreateEntry("manifest.json");
        RecoveryEvidencePackageValidationResult result = RecoveryEvidencePackage.Validate(duplicate);
        Assert.False(result.IsValid);
        Assert.Contains("duplicate", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateEvidencePackage_RejectsUnsupportedSchema()
    {
        using var temp = new TempDirectory();
        string package = RecoveryEvidencePackage.Export(CreateSnapshot(), CreateLabels(), temp.Path);
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            ZipArchiveEntry evidence = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("recovery-evidence.json"));
            evidence.Delete();
            ZipArchiveEntry replacement = archive.CreateEntry("recovery-evidence.json");
            using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
            writer.Write("{\"schemaVersion\":2}");
        }

        RecoveryEvidencePackageValidationResult result = RecoveryEvidencePackage.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains("unsupported schema", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry(name));
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static RecoveryReportSnapshot CreateSnapshot() =>
        new(
            new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero),
            78,
            "Review",
            "Recovery baseline needs review",
            "Ready 1 - Attention 1 - Risk 0 - Unavailable 0",
            "One project should be reviewed.",
            2,
            1,
            1,
            0,
            0,
            1,
            2,
            2,
            2,
            "Protect a known-good restore point.",
            [
                new RecoveryReportProject("Project Alpha", "Review", 72, "No protected restore point."),
                new RecoveryReportProject("Project Beta", "Clean", 84, "Recent verified backup.")
            ]);

    private static RecoveryReportLabels CreateLabels() =>
        new(
            "VaultSync Recovery Report",
            "Generated",
            "Recovery overview",
            "Readiness",
            "Projects measured",
            "Recovery coverage",
            "Top recommendation",
            "Project recovery matrix",
            "Project",
            "Status",
            "Score",
            "Reason",
            "No projects measured.");
}
