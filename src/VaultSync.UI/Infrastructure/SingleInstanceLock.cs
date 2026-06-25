using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace VaultSync.UI.Infrastructure;

internal sealed class SingleInstanceLock : IDisposable
{
    private static readonly object LinuxPathGate = new();
    private static readonly HashSet<string> OwnedLinuxPaths = new(StringComparer.Ordinal);

    private readonly Mutex? _mutex;
    private readonly FileStream? _linuxLockStream;
    private readonly string? _linuxLockPath;
    private bool _ownsLock;

    private SingleInstanceLock(Mutex mutex, bool ownsLock)
    {
        _mutex = mutex;
        _ownsLock = ownsLock;
    }

    private SingleInstanceLock(FileStream linuxLockStream, string linuxLockPath)
    {
        _linuxLockStream = linuxLockStream;
        _linuxLockPath = linuxLockPath;
        _ownsLock = true;
    }

    private SingleInstanceLock()
    {
    }

    internal bool IsAcquired => _ownsLock;

    internal static SingleInstanceLock TryAcquire(string mutexName, string linuxLockFileName)
    {
        if (OperatingSystem.IsLinux())
            return TryAcquireLinux(linuxLockFileName);

        var mutex = new Mutex(true, mutexName, out bool createdNew);
        return new SingleInstanceLock(mutex, createdNew);
    }

    [SupportedOSPlatform("linux")]
    internal static SingleInstanceLock TryAcquireLinux(string lockFileName, string? lockDirectory = null)
    {
        string directory = lockDirectory ?? ResolveLinuxLockDirectory();
        Directory.CreateDirectory(directory);

        string lockPath = Path.GetFullPath(Path.Combine(directory, lockFileName));
        lock (LinuxPathGate)
        {
            if (!OwnedLinuxPaths.Add(lockPath))
                return new SingleInstanceLock();
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            stream.Lock(0, 1);
            return new SingleInstanceLock(stream, lockPath);
        }
        catch (IOException)
        {
            stream?.Dispose();
            ReleaseLinuxPathReservation(lockPath);
            return new SingleInstanceLock();
        }
        catch
        {
            stream?.Dispose();
            ReleaseLinuxPathReservation(lockPath);
            throw;
        }
    }

    private static string ResolveLinuxLockDirectory()
    {
        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDirectory) && Path.IsPathFullyQualified(runtimeDirectory))
            return Path.Combine(runtimeDirectory, "vaultsync");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VaultSync",
            "runtime");
    }

    public void Dispose()
    {
        if (!_ownsLock)
        {
            _mutex?.Dispose();
            return;
        }

        _ownsLock = false;

        if (_linuxLockStream is not null)
        {
            _linuxLockStream.Dispose();
            if (_linuxLockPath is not null)
                ReleaseLinuxPathReservation(_linuxLockPath);
            return;
        }

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }

    private static void ReleaseLinuxPathReservation(string lockPath)
    {
        lock (LinuxPathGate)
        {
            OwnedLinuxPaths.Remove(lockPath);
        }
    }
}
