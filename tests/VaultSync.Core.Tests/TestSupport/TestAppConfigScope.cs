using System;
using VaultSync.Core.Config;

namespace VaultSync.Core.Tests.TestSupport;

public sealed class TestAppConfigScope : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly IDisposable _configScope;
    private bool _disposed;

    public TestAppConfigScope()
    {
        _configScope = AppConfigStore.UseDirectoryForTests(_directory.Path);
    }

    public string ConfigDirectory => _directory.Path;

    public void Dispose()
    {
        if (_disposed)
            return;

        _configScope.Dispose();
        _directory.Dispose();
        _disposed = true;
    }
}
