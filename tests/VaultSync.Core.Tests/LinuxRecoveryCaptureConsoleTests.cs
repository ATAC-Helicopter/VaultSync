#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryCaptureConsoleTests
{
    private static string? Text(string key) => key switch
    {
        "Recovery.Capture.Title" => "Offline capture",
        "Recovery.Capture.Warning" => "Raw deleted data; Ctrl+C cancels.",
        "Recovery.Capture.Source" => "Source {0}: {1} bytes; sectors {2}/{3}.",
        "Recovery.Capture.Challenge" => "Type {0}; blank cancels.",
        "Recovery.Capture.NoSources" => "No safe source.",
        "Recovery.Capture.Cancelled" => "Capture cancelled.",
        _ => null
    };
    private static LinuxBlockInventory Inventory() => new(LinuxBlockInventoryState.Observed,
        LinuxBlockInventoryReason.NamespaceObservationOnly, Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(),
        "mnt:[12]", false, new[] { new LinuxBlockObservation("vdc", "8:32", null, LinuxBlockKind.WholeDisk,
            65536, 512, 512, 4, true, "lab-source", LinuxBlockIdentity.UniqueInSnapshot,
            false, false, false, LinuxBlockUse.Unknown) });

    [Fact]
    public async Task OnlyExactPhysicalSourceAndOperationChallengeSelects()
    {
        Guid operation = Guid.NewGuid();
        var access = new LinuxRecoveryLocalConsoleTests.Access { AfterPrompt = Encoding.ASCII.GetBytes("vdc " + operation + "\n") };
        access.Input.Enqueue((byte)'x');
        string? selected = await LinuxRecoveryCaptureConsole.SelectAsync(operation, Inventory(), Text,
            CultureInfo.GetCultureInfo("it"), () => access, CancellationToken.None);
        Assert.Equal("vdc", selected); Assert.True(access.Disposed); Assert.True(access.Flushed);
        Assert.Contains("65.536", access.Written); Assert.Equal(3, access.Validations);
    }

    [Theory]
    [InlineData("")] [InlineData("vdc")] [InlineData("vdc wrong")]
    [InlineData("vda {0}")] [InlineData("vdc {0}\nextra")]
    public async Task BlankDeclinesAndWrongOrExtraInputNeverStartsCapture(string input)
    {
        Guid operation = Guid.NewGuid();
        var access = new LinuxRecoveryLocalConsoleTests.Access { AfterPrompt = Encoding.ASCII.GetBytes(input.Replace("{0}", operation.ToString()) + "\n") };
        var pending = LinuxRecoveryCaptureConsole.SelectAsync(operation, Inventory(), Text,
            CultureInfo.InvariantCulture, () => access, CancellationToken.None);
        if (input.Length == 0) { Assert.Null(await pending); Assert.Contains("Capture cancelled.", access.Written); }
        else await Assert.ThrowsAsync<InvalidDataException>(() => pending);
        Assert.True(access.Disposed);
    }

    [Fact]
    public async Task CancelledInputDrainsAndReleasesPhysicalConsole()
    {
        using var stop = new CancellationTokenSource();
        var access = new LinuxRecoveryLocalConsoleTests.Access { WaitForInput = true };
        var pending = LinuxRecoveryCaptureConsole.SelectAsync(Guid.NewGuid(), Inventory(), Text,
            CultureInfo.InvariantCulture, () => access, stop.Token);
        await access.Prompted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(access.Disposed);
    }

    [Theory]
    [InlineData("writable")] [InlineData("mounted")] [InlineData("partition")]
    [InlineData("ambiguous")] [InlineData("stacked")] [InlineData("control")]
    public async Task UnsafeSourcesDisplayNoSourcesAndNeverOfferChallenge(string change)
    {
        var inventory = Inventory(); var source = inventory.Devices[0];
        source = change switch
        {
            "writable" => source with { IsReadOnly = false },
            "mounted" => source with { MountObserved = true },
            "partition" => source with { Kind = LinuxBlockKind.Partition },
            "ambiguous" => source with { Identity = LinuxBlockIdentity.Ambiguous },
            "stacked" => source with { Kind = LinuxBlockKind.VirtualOrStacked },
            _ => source with { KernelName = "vdc\n" }
        };
        // Output-only path does not flush input; the shared prompt fake requires this flag.
        var access = new LinuxRecoveryLocalConsoleTests.Access { Flushed = true };
        Assert.Null(await LinuxRecoveryCaptureConsole.SelectAsync(Guid.NewGuid(), inventory with { Devices = new[] { source } },
            Text, CultureInfo.InvariantCulture, () => access, CancellationToken.None));
        Assert.Equal("No safe source.\n", access.Written);
        Assert.True(access.Disposed);
    }

    [Theory]
    [InlineData("missing {0}", 2)] [InlineData("invalid {2}", 2)] [InlineData("control\n{0}", 1)]
    [InlineData("format {0:N0}", 1)]
    public void DamagedCatalogCannotHideCaptureReviewFields(string template, int count) =>
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryCaptureConsole.Format(_ => template,
            CultureInfo.InvariantCulture, "Source", new object[count]));

    [Theory]
    [InlineData("headless")] [InlineData("graphical")] [InlineData("")]
    public void CaptureEntryRefusesRestoreInstallationModes(string mode) =>
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryCaptureHost.RequireMode(mode));
}
