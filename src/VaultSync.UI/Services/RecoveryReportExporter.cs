using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VaultSync.UI.Services;

internal sealed record RecoveryReportProject(
    string ProjectName,
    string Status,
    int Score,
    string Reason,
    string Copies = "",
    string Media = "",
    string Offsite = "",
    string LastDrill = "");

internal sealed record RecoveryReportSnapshot(
    DateTimeOffset GeneratedAt,
    int ReadinessPercent,
    string ReadinessBand,
    string Headline,
    string Detail,
    string Insight,
    int ProjectCount,
    int ReadyCount,
    int AttentionCount,
    int RiskCount,
    int UnavailableCount,
    int Coverage24Hours,
    int Coverage7Days,
    int Coverage30Days,
    int Coverage90Days,
    string TopRecommendation,
    IReadOnlyList<RecoveryReportProject> Projects,
    int ThreeTwoOneReadyCount = 0,
    int DrilledProjectCount = 0,
    int PassedDrillCount = 0,
    int ProtectedPointCount = 0);

internal sealed record RecoveryReportLabels(
    string Title,
    string Generated,
    string Overview,
    string Readiness,
    string Projects,
    string Coverage,
    string Recommendation,
    string ProjectMatrix,
    string Project,
    string Status,
    string Score,
    string Reason,
    string NoProjects,
    string Protection = "Disaster recovery protection",
    string ThreeTwoOne = "3-2-1 ready",
    string Drills = "Recovery drills",
    string ProtectedPoints = "Protected recovery points",
    string Copies = "Copies",
    string Media = "Media",
    string Offsite = "Offsite",
    string LastDrill = "Last drill");

internal static class RecoveryReportExporter
{
    public static string ExportMarkdown(
        RecoveryReportSnapshot snapshot,
        RecoveryReportLabels labels,
        string? exportRoot = null)
    {
        string root = string.IsNullOrWhiteSpace(exportRoot)
            ? GetDefaultExportDirectory()
            : exportRoot;
        Directory.CreateDirectory(root);

        string fileName = $"VaultSync-Recovery-{snapshot.GeneratedAt:yyyyMMdd-HHmmss}.md";
        string path = EnsureUniquePath(Path.Combine(root, fileName));
        File.WriteAllText(path, BuildMarkdown(snapshot, labels), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public static string BuildMarkdown(RecoveryReportSnapshot snapshot, RecoveryReportLabels labels)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(labels.Title);
        builder.AppendLine();
        builder.Append("**").Append(labels.Generated).Append(":** ")
            .AppendLine(snapshot.GeneratedAt.ToLocalTime().ToString("F", CultureInfo.CurrentCulture));
        builder.AppendLine();

        builder.Append("## ").AppendLine(labels.Overview);
        builder.AppendLine();
        builder.Append("- **").Append(labels.Readiness).Append(":** ")
            .Append(snapshot.ReadinessPercent).Append("% (").Append(snapshot.ReadinessBand).AppendLine(")");
        builder.Append("- **").Append(labels.Projects).Append(":** ")
            .Append(snapshot.ProjectCount).AppendLine();
        builder.AppendLine();
        builder.AppendLine(snapshot.Headline);
        builder.AppendLine();
        builder.AppendLine(snapshot.Detail);
        builder.AppendLine();
        builder.AppendLine(snapshot.Insight);
        builder.AppendLine();

        builder.Append("## ").AppendLine(labels.Coverage);
        builder.AppendLine();
        AppendCoverage(builder, "24h", snapshot.Coverage24Hours, snapshot.ProjectCount);
        AppendCoverage(builder, "7d", snapshot.Coverage7Days, snapshot.ProjectCount);
        AppendCoverage(builder, "30d", snapshot.Coverage30Days, snapshot.ProjectCount);
        AppendCoverage(builder, "90d", snapshot.Coverage90Days, snapshot.ProjectCount);
        builder.AppendLine();

        builder.Append("## ").AppendLine(labels.Recommendation);
        builder.AppendLine();
        builder.AppendLine(snapshot.TopRecommendation);
        builder.AppendLine();

        builder.Append("## ").AppendLine(labels.Protection);
        builder.AppendLine();
        builder.Append("- **").Append(labels.ThreeTwoOne).Append(":** ")
            .Append(snapshot.ThreeTwoOneReadyCount).Append('/').Append(snapshot.ProjectCount).AppendLine();
        builder.Append("- **").Append(labels.Drills).Append(":** ")
            .Append(snapshot.PassedDrillCount).Append(" passed / ").Append(snapshot.DrilledProjectCount).AppendLine(" run");
        builder.Append("- **").Append(labels.ProtectedPoints).Append(":** ")
            .Append(snapshot.ProtectedPointCount).AppendLine();
        builder.AppendLine();

        builder.Append("## ").AppendLine(labels.ProjectMatrix);
        builder.AppendLine();
        if (snapshot.Projects.Count == 0)
        {
            builder.AppendLine(labels.NoProjects);
            return builder.ToString();
        }

        builder.Append("| ").Append(labels.Project)
            .Append(" | ").Append(labels.Status)
            .Append(" | ").Append(labels.Score)
            .Append(" | ").Append(labels.Copies)
            .Append(" | ").Append(labels.Media)
            .Append(" | ").Append(labels.Offsite)
            .Append(" | ").Append(labels.LastDrill)
            .Append(" | ").Append(labels.Reason).AppendLine(" |");
        builder.AppendLine("|---|---:|---:|---:|---:|---|---|---|");
        foreach (RecoveryReportProject project in snapshot.Projects)
        {
            builder.Append("| ").Append(EscapeCell(project.ProjectName))
                .Append(" | ").Append(EscapeCell(project.Status))
                .Append(" | ").Append(project.Score).Append("%")
                .Append(" | ").Append(EscapeCell(project.Copies))
                .Append(" | ").Append(EscapeCell(project.Media))
                .Append(" | ").Append(EscapeCell(project.Offsite))
                .Append(" | ").Append(EscapeCell(project.LastDrill))
                .Append(" | ").Append(EscapeCell(project.Reason)).AppendLine(" |");
        }

        return builder.ToString();
    }

    private static void AppendCoverage(StringBuilder builder, string window, int covered, int total)
    {
        int percent = total <= 0 ? 0 : (int)Math.Round(covered * 100.0 / total);
        builder.Append("- **").Append(window).Append(":** ")
            .Append(covered).Append('/').Append(total)
            .Append(" (").Append(percent).AppendLine("%)");
    }

    private static string EscapeCell(string value) =>
        (value ?? string.Empty)
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Trim();

    private static string GetDefaultExportDirectory()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Path.GetTempPath();

        return Path.Combine(documents, "VaultSync", "Exports", "Recovery");
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        return Enumerable.Range(2, int.MaxValue - 1)
            .Select(index => Path.Combine(directory, $"{name}-{index}{extension}"))
            .First(candidate => !File.Exists(candidate));
    }
}
