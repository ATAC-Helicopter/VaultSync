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
        Assert.Contains("Crash reason: System.InvalidOperationException", report.Content, StringComparison.Ordinal);
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
        Assert.Matches("^CRASH-UI-[0-9A-F]{32}$", report.ReportId);
        Assert.Equal("user-interface", report.CrashCategory);
        Assert.Equal("System.InvalidOperationException", report.CrashReason);
        Assert.Contains(report.OperatingSystemFamily, new[] { "Windows", "macOS", "Linux", "Other" });
    }

    [Fact]
    public void Create_AssignsAUniqueCategoryPrefixedIdentityPerLocalReport()
    {
        Exception exception = CaptureException("not included");

        CrashReportDocument first = ShareableCrashReport.Create(exception, "AppDomain", true, "1.8.4");
        CrashReportDocument second = ShareableCrashReport.Create(exception, "AppDomain", true, "1.8.4");

        Assert.Matches("^CRASH-APP-[0-9A-F]{32}$", first.ReportId);
        Assert.Matches("^CRASH-APP-[0-9A-F]{32}$", second.ReportId);
        Assert.NotEqual(first.ReportId, second.ReportId);
    }

    [Fact]
    public void EmailDraftFields_ContainOnlyReportIdAndGenericInstructions()
    {
        var report = new CrashReportDocument(
            "CRASH-UI-00112233445566778899AABBCCDDEEFF",
            "macOS",
            "user-interface",
            "System.InvalidOperationException",
            "sensitive content that must not enter the mailto URI");

        string subject = ShareableCrashReport.BuildEmailSubject(report);
        string body = ShareableCrashReport.BuildEmailBody();

        Assert.Contains(report.ReportId, subject, StringComparison.Ordinal);
        Assert.Contains("already attached", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive content", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive content", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MacMailDraft_PassesAttachmentAsAnOpaqueArgument()
    {
        const string attachment = "/tmp/VaultSync report 'review'.txt";

        var startInfo = CrashReportEmailDraft.CreateMacMailStartInfo("subject", "body", attachment);

        Assert.Equal("/usr/bin/osascript", startInfo.FileName);
        Assert.Equal(attachment, startInfo.ArgumentList[^1]);
        Assert.DoesNotContain(attachment, startInfo.ArgumentList[1], StringComparison.Ordinal);
        Assert.Contains("make new attachment", startInfo.ArgumentList[1], StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxMailDraft_UsesAttachmentOptionWithoutShellInterpolation()
    {
        const string attachment = "/tmp/VaultSync report.txt";

        var startInfo = CrashReportEmailDraft.CreateLinuxMailStartInfo("subject", "body", attachment);

        int attachmentOption = startInfo.ArgumentList.IndexOf("--attach");
        Assert.Equal("xdg-email", startInfo.FileName);
        Assert.True(attachmentOption >= 0);
        Assert.Equal(attachment, startInfo.ArgumentList[attachmentOption + 1]);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void WindowsMailDraft_AddsTheReviewedReportThroughOutlook()
    {
        const string attachment = @"C:\Reports\VaultSync report.txt";

        var startInfo = CrashReportEmailDraft.CreateWindowsOutlookStartInfo("subject", "body", attachment);

        Assert.Equal("powershell.exe", startInfo.FileName);
        Assert.Equal(attachment, startInfo.ArgumentList[^1]);
        Assert.Contains("Attachments.Add", startInfo.ArgumentList[^5], StringComparison.Ordinal);
        Assert.False(startInfo.UseShellExecute);
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
    public void Save_UsesPrivateBoundedManagedStorageAndPreservesLockedContent()
    {
        using var temp = new TempDirectory();

        string latestPath = string.Empty;
        for (int index = 0; index < 12; index++)
        {
            var report = CreateDocument($"CRASH-GEN-{index:X32}", $"locked {index}");
            latestPath = ShareableCrashReport.Save(report, temp.Path);
        }

        Assert.NotEmpty(latestPath);
        Assert.Equal("locked 11", File.ReadAllText(latestPath));
        Assert.True(Directory.GetFiles(temp.Path, "vaultsync-crash-*.txt").Length <= 10);

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(latestPath);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }

        Assert.True(ShareableCrashReport.DeleteSavedReport(latestPath, temp.Path));
        Assert.False(File.Exists(latestPath));
    }

    private static CrashReportDocument CreateDocument(string reportId, string content) => new(
        reportId,
        "Other",
        "application",
        "System.Exception",
        content);

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
