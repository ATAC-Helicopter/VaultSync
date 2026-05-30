using System;
using System.IO;
using System.Threading;

namespace VaultSync.Core.Tests.TestSupport;

public sealed class TempDirectory : IDisposable
{
    private const int MaxDeleteAttempts = 3;

    public TempDirectory()
        : this(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vaultsync-test-{Guid.NewGuid():N}"))
    {
    }

    public TempDirectory(string path)
    {
        Path = path;
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        for (int attempt = 1; attempt <= MaxDeleteAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);

                return;
            }
            catch when (attempt < MaxDeleteAttempts)
            {
                Thread.Sleep(25 * attempt);
            }
            catch
            {
                // Ignore cleanup failures in tests.
            }
        }
    }
}
