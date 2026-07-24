using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VaultSync.UI.Infrastructure;

internal static class CrashReportEmailDraft
{
    private static readonly TimeSpan DraftTimeout = TimeSpan.FromSeconds(20);

    public static async Task<bool> PrepareAsync(
        CrashReportDocument report,
        string attachmentPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        string fullPath = Path.GetFullPath(attachmentPath);
        if (!File.Exists(fullPath))
            return false;

        ProcessStartInfo? startInfo = CreateStartInfo(report, fullPath);
        if (startInfo is null)
            return false;

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DraftTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static ProcessStartInfo? CreateStartInfo(CrashReportDocument report, string attachmentPath)
    {
        string subject = ShareableCrashReport.BuildEmailSubject(report);
        string body = ShareableCrashReport.BuildEmailBody();

        if (OperatingSystem.IsMacOS())
            return CreateMacMailStartInfo(subject, body, attachmentPath);
        if (OperatingSystem.IsLinux())
            return CreateLinuxMailStartInfo(subject, body, attachmentPath);
        if (OperatingSystem.IsWindows())
            return CreateWindowsOutlookStartInfo(subject, body, attachmentPath);
        return null;
    }

    internal static ProcessStartInfo CreateMacMailStartInfo(
        string subject,
        string body,
        string attachmentPath)
    {
        const string script = """
            on run argv
                set recipientAddress to item 1 of argv
                set messageSubject to item 2 of argv
                set messageBody to item 3 of argv
                set attachmentPath to item 4 of argv
                set attachmentFile to POSIX file attachmentPath as alias
                tell application "Mail"
                    set draftMessage to make new outgoing message with properties {subject:messageSubject, content:messageBody & return & return, visible:true}
                    tell draftMessage
                        make new to recipient at end of to recipients with properties {address:recipientAddress}
                        tell content
                            make new attachment with properties {file name:attachmentFile} at after last paragraph
                        end tell
                    end tell
                    activate
                end tell
            end run
            """;

        var startInfo = CreateRedirectedStartInfo("/usr/bin/osascript");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("--");
        AddDraftArguments(startInfo, subject, body, attachmentPath);
        return startInfo;
    }

    internal static ProcessStartInfo CreateLinuxMailStartInfo(
        string subject,
        string body,
        string attachmentPath)
    {
        var startInfo = CreateRedirectedStartInfo("xdg-email");
        startInfo.ArgumentList.Add("--utf8");
        startInfo.ArgumentList.Add("--subject");
        startInfo.ArgumentList.Add(subject);
        startInfo.ArgumentList.Add("--body");
        startInfo.ArgumentList.Add(body);
        startInfo.ArgumentList.Add("--attach");
        startInfo.ArgumentList.Add(attachmentPath);
        startInfo.ArgumentList.Add(ShareableCrashReport.SupportAddress);
        return startInfo;
    }

    internal static ProcessStartInfo CreateWindowsOutlookStartInfo(
        string subject,
        string body,
        string attachmentPath)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $outlook = New-Object -ComObject Outlook.Application
            $message = $outlook.CreateItem(0)
            $message.To = $args[0]
            $message.Subject = $args[1]
            $message.Body = $args[2]
            [void]$message.Attachments.Add($args[3])
            $message.Display()
            """;

        var startInfo = CreateRedirectedStartInfo("powershell.exe");
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Sta");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        AddDraftArguments(startInfo, subject, body, attachmentPath);
        return startInfo;
    }

    private static ProcessStartInfo CreateRedirectedStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private static void AddDraftArguments(
        ProcessStartInfo startInfo,
        string subject,
        string body,
        string attachmentPath)
    {
        startInfo.ArgumentList.Add(ShareableCrashReport.SupportAddress);
        startInfo.ArgumentList.Add(subject);
        startInfo.ArgumentList.Add(body);
        startInfo.ArgumentList.Add(attachmentPath);
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort only; draft preparation must not destabilize crash handling.
        }
    }
}
