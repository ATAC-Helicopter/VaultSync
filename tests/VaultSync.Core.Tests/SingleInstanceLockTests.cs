using System;
using System.Diagnostics;
using System.IO;
using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SingleInstanceLockTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), $"vaultsync-single-instance-{Guid.NewGuid():N}");

    [Fact]
    public void LinuxLock_BlocksSecondOwner_AndCanBeReacquiredAfterDispose()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var first = SingleInstanceLock.TryAcquireLinux("app.lock", _tempDirectory);
        using var second = SingleInstanceLock.TryAcquireLinux("app.lock", _tempDirectory);

        Assert.True(first.IsAcquired);
        Assert.False(second.IsAcquired);

        first.Dispose();

        using var third = SingleInstanceLock.TryAcquireLinux("app.lock", _tempDirectory);
        Assert.True(third.IsAcquired);
    }

    [Fact]
    public void LinuxLock_BlocksAnotherProcess()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/python3"))
            return;

        string lockPath = Path.Combine(_tempDirectory, "process.lock");
        using var owner = SingleInstanceLock.TryAcquireLinux("process.lock", _tempDirectory);

        Assert.True(owner.IsAcquired);
        Assert.Equal(1, TryAcquireWithPython(lockPath));

        owner.Dispose();

        Assert.Equal(0, TryAcquireWithPython(lockPath));
    }

    private static int TryAcquireWithPython(string lockPath)
    {
        const string script =
            "import fcntl,sys; f=open(sys.argv[1],'r+'); " +
            "\ntry: fcntl.lockf(f,fcntl.LOCK_EX|fcntl.LOCK_NB); sys.exit(0)" +
            "\nexcept BlockingIOError: sys.exit(1)";
        var startInfo = new ProcessStartInfo("/usr/bin/python3")
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(lockPath);

        using Process process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
