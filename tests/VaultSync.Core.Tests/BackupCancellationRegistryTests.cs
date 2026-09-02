using System.Threading;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupCancellationRegistryTests
{
    [Fact]
    public void Register_OverlappingProjectRunCancelsPreviousRegistration()
    {
        var registry = new BackupCancellationRegistry();
        using BackupCancellationRegistry.Registration first =
            registry.Register(42, CancellationToken.None);

        using BackupCancellationRegistry.Registration second =
            registry.Register(42, CancellationToken.None);

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_OlderRegistrationCannotRemoveNewerProjectRun()
    {
        var registry = new BackupCancellationRegistry();
        BackupCancellationRegistry.Registration first =
            registry.Register(42, CancellationToken.None);
        using BackupCancellationRegistry.Registration second =
            registry.Register(42, CancellationToken.None);

        first.Dispose();
        Assert.True(registry.Cancel(42));
        Assert.True(second.Token.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_CurrentRegistrationRemovesItFromRegistry()
    {
        var registry = new BackupCancellationRegistry();
        BackupCancellationRegistry.Registration registration =
            registry.Register(42, CancellationToken.None);

        registration.Dispose();

        Assert.False(registry.Cancel(42));
    }

    [Fact]
    public void Register_LinksCallerCancellation()
    {
        var registry = new BackupCancellationRegistry();
        using var caller = new CancellationTokenSource();
        using BackupCancellationRegistry.Registration registration =
            registry.Register(42, caller.Token);

        caller.Cancel();

        Assert.True(registration.Token.IsCancellationRequested);
    }
}
