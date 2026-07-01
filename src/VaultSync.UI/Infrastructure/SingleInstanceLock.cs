using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace VaultSync.UI.Infrastructure;

internal sealed class SingleInstanceLock : IDisposable
{
    private static readonly object FileLockPathGate = new();
    private static readonly HashSet<string> OwnedFileLockPaths = new(StringComparer.Ordinal);

    private readonly Mutex? _mutex;
    private readonly FileStream? _fileLockStream;
    private readonly string? _fileLockPath;
    private bool _ownsLock;

    private SingleInstanceLock(Mutex mutex, bool ownsLock)
    {
        _mutex = mutex;
        _ownsLock = ownsLock;
    }

    private SingleInstanceLock(FileStream fileLockStream, string fileLockPath)
    {
        _fileLockStream = fileLockStream;
        _fileLockPath = fileLockPath;
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

        if (OperatingSystem.IsMacOS())
            return TryAcquireMacOs(linuxLockFileName);

        var mutex = new Mutex(true, mutexName, out bool createdNew);
        return new SingleInstanceLock(mutex, createdNew);
    }

    [SupportedOSPlatform("linux")]
    internal static SingleInstanceLock TryAcquireLinux(string lockFileName, string? lockDirectory = null)
        => TryAcquireFileLock(lockFileName, lockDirectory);

    [SupportedOSPlatform("linux")]
    internal static SingleInstanceLock TryAcquireFileLock(string lockFileName, string? lockDirectory = null)
    {
        string directory = lockDirectory ?? ResolveFileLockDirectory();
        Directory.CreateDirectory(directory);

        string lockPath = Path.GetFullPath(Path.Combine(directory, lockFileName));
        lock (FileLockPathGate)
        {
            if (!OwnedFileLockPaths.Add(lockPath))
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
            ReleaseFileLockPathReservation(lockPath);
            return new SingleInstanceLock();
        }
        catch
        {
            stream?.Dispose();
            ReleaseFileLockPathReservation(lockPath);
            throw;
        }
    }

    internal static SingleInstanceLock TryAcquireMacOs(string lockFileName, string? lockDirectory = null)
    {
        string directory = lockDirectory ?? ResolveFileLockDirectory();
        Directory.CreateDirectory(directory);

        string lockPath = Path.GetFullPath(Path.Combine(directory, lockFileName));
        lock (FileLockPathGate)
        {
            if (!OwnedFileLockPaths.Add(lockPath))
                return new SingleInstanceLock();
        }

        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return new SingleInstanceLock(stream, lockPath);
        }
        catch (IOException)
        {
            ReleaseFileLockPathReservation(lockPath);
            return new SingleInstanceLock();
        }
        catch
        {
            ReleaseFileLockPathReservation(lockPath);
            throw;
        }
    }

    private static string ResolveFileLockDirectory()
    {
        if (OperatingSystem.IsLinux())
        {
            string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtimeDirectory) && Path.IsPathFullyQualified(runtimeDirectory))
                return Path.Combine(runtimeDirectory, "vaultsync");
        }

        string? tempDirectory = Environment.GetEnvironmentVariable("TMPDIR");
        if (OperatingSystem.IsMacOS() &&
            !string.IsNullOrWhiteSpace(tempDirectory) &&
            Path.IsPathFullyQualified(tempDirectory))
        {
            return Path.Combine(tempDirectory, "vaultsync");
        }

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

        if (_fileLockStream is not null)
        {
            _fileLockStream.Dispose();
            if (_fileLockPath is not null)
                ReleaseFileLockPathReservation(_fileLockPath);
            return;
        }

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }

    private static void ReleaseFileLockPathReservation(string lockPath)
    {
        lock (FileLockPathGate)
        {
            OwnedFileLockPaths.Remove(lockPath);
        }
    }
}
