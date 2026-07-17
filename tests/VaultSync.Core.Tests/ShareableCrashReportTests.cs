using System;
using System.IO;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ShareableCrashReportTests
{
    [Fact]
    public void Create_UsesStrictAllowlistAndDropsSensitiveExceptionContext()
    {
        const string sensitiveMessage =
            "Backup Project Falcon failed at /Users/private-name/Documents/Secret " +
            "for person@example.com on 192.168.1.44 with token=super-secret";

        Exception exception = CaptureException(sensitiveMessage);
        CrashReportDocument report = ShareableCrashReport.Create(
            exception,
            "UI thread",
            isTerminating: true,
            "VaultSync 1.8.4.123");

        Assert.Contains("Application version: 1.8.4.123", report.Content, StringComparison.Ordinal);
        Assert.Contains("Crash category: user-interface", report.Content, StringComparison.Ordinal);
        Assert.Contains("Exception 1: System.InvalidOperationException", report.Content, StringComparison.Ordinal);
        Assert.Contains("ShareableCrashReportTests.CaptureException()", report.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Project Falcon", report.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("private-name", report.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.com", report.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.44", report.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", report.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.OSVersion.VersionString, report.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, report.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, report.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildEmailUri_ContainsOnlyRecipientReportIdAndGenericInstructions()
    {
        var report = new CrashReportDocument(
            "A1B2C3D4",
            "sensitive content that must not enter the mailto URI");

        string uri = ShareableCrashReport.BuildEmailUri(report);

        Assert.StartsWith("mailto:crash-reports@fglabs.dev?", uri, StringComparison.Ordinal);
        Assert.Contains("A1B2C3D4", Uri.UnescapeDataString(uri), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive content", Uri.UnescapeDataString(uri), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteSavedReport_RejectsFilesOutsideManagedReportDirectory()
    {
        string outsidePath = Path.Combine(Path.GetTempPath(), $"vaultsync-outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outsidePath, "do not delete");
        try
        {
            Assert.False(ShareableCrashReport.DeleteSavedReport(outsidePath));
            Assert.True(File.Exists(outsidePath));
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public void Save_UsesPrivateBoundedManagedStorageAndPreservesVisibleEdits()
    {
        using var temp = new TempDirectory();

        string latestPath = string.Empty;
        for (int index = 0; index < 12; index++)
        {
            var report = new CrashReportDocument($"{index:X8}", "original");
            latestPath = ShareableCrashReport.Save(report, $"edited {index}", temp.Path);
        }

        Assert.NotEmpty(latestPath);
        Assert.Equal("edited 11", File.ReadAllText(latestPath));
        Assert.True(Directory.GetFiles(temp.Path, "vaultsync-crash-*.txt").Length <= 10);

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(latestPath);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }

        Assert.True(ShareableCrashReport.DeleteSavedReport(latestPath, temp.Path));
        Assert.False(File.Exists(latestPath));
    }

    private static Exception CaptureException(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
