using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RsyncRunnerTests
{
    [Fact]
    public async Task SyncAsync_CancellationStopsRunningTool()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TempDirectory();
        string source = Path.Combine(temp.Path, "source");
        string destination = Path.Combine(temp.Path, "destination");
        string toolPath = Path.Combine(temp.Path, "rsync-cancellation-test-tool");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(toolPath, """
            #!/bin/sh
            if [ "$1" = "--version" ]; then
              echo "rsync version 3.2.7 protocol version 31"
              exit 0
            fi
            sleep 10
            exit 0
            """);
        File.SetUnixFileMode(
            toolPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var project = new Project
        {
            Id = 43,
            Name = "Rsync cancellation test",
            RootPath = source,
            Preset = string.Empty
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RsyncRunner(rsyncPath: toolPath).SyncAsync(
                project,
                destination,
                dryRun: true,
                ct: cancellation.Token));
    }

    [Fact]
    public async Task SyncAsync_UsesQualifiedToolAndReportsStreamedProgress()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TempDirectory();
        string source = Path.Combine(temp.Path, "source");
        string destination = Path.Combine(temp.Path, "destination");
        string toolPath = Path.Combine(temp.Path, "rsync-test-tool");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, ".vaultsyncignore"), "*.tmp\n");
        File.WriteAllText(toolPath, """
            #!/bin/sh
            if [ "$1" = "--version" ]; then
              echo "rsync version 3.2.7 protocol version 31"
              exit 0
            fi
            echo "folder/file.txt"
            echo "100%"
            exit 0
            """);
        File.SetUnixFileMode(
            toolPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var project = new Project
        {
            Id = 42,
            Name = "Rsync test",
            RootPath = source,
            Preset = string.Empty
        };
        var updates = new List<(double Percent, string File)>();
        var runner = new RsyncRunner(rsyncPath: toolPath);

        int exitCode = await runner.SyncAsync(
            project,
            destination,
            dryRun: false,
            (percent, file, _) => updates.Add((percent, file)),
            ct: CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(updates, update => update.Percent == 100 && update.File == "folder/file.txt");
    }
}
