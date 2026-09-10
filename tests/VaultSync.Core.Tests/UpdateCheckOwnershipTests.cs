#nullable enable

using System.Threading;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class UpdateCheckOwnershipTests
{
    [Fact]
    public void OlderCheckCannotReleaseReplacementOwnership()
    {
        using var older = new CancellationTokenSource();
        using var replacement = new CancellationTokenSource();
        CancellationTokenSource? active = replacement;

        bool released = AppViewModel.TryReleaseUpdateCheckOwnership(ref active, older);

        Assert.False(released);
        Assert.Same(replacement, active);
    }

    [Fact]
    public void CurrentCheckReleasesOnlyItsOwnSlot()
    {
        using var owner = new CancellationTokenSource();
        CancellationTokenSource? active = owner;

        bool released = AppViewModel.TryReleaseUpdateCheckOwnership(ref active, owner);

        Assert.True(released);
        Assert.Null(active);
    }
}
