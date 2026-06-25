#nullable enable

using System;
using System.IO;
using VaultSync.Core.Tests.TestSupport;
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
