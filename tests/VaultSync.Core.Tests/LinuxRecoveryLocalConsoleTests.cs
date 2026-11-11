#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;
using ConsolePolicy = VaultSync.Core.Services.LinuxRecoveryLocalConsole;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryLocalConsoleTests
{
    internal static readonly ConsolePolicy.Context Context = new(0, 0, 0, 0,
        new(0x7ff, 0x2180, 0, 1, 4, 1, 123, 0, 5), new(0x7ff, 0x2180, 0, 1, 4, 1, 123, 0, 5),
        true, 42, 42, 42, 42, 1, true, true);
    // Fixture catalog only. Production has no embedded/fallback English strings.
    internal static string? Text(string key) => key switch
    {
        "Recovery.Console.Title" => "Confirm full disk overwrite",
        "Recovery.Console.Warning" => "This permanently overwrites every byte on the selected disk.",
        "Recovery.Console.Target" => "Target: {0} ({1}), disk sequence {2}",
        "Recovery.Console.Capacity" => "Overwrite: {0} bytes; logical sector {1} bytes; physical sector {2} bytes",
        "Recovery.Console.Image" => "Image: {0}; capture: {1}; generation: {2}",
        "Recovery.Console.Manifest" => "Manifest SHA-256: {0}",
        "Recovery.Console.Hardware" => "Hardware identity: {0}",
        "Recovery.Console.Challenge" => "Type exactly {0} and press Enter to confirm.",
        "Recovery.Console.Deadline" => "Complete confirmation before this review expires.",
        _ => null
    };

    internal sealed class Access : ConsolePolicy.IAccess
    {
        internal ConsolePolicy.Context Current = Context;
        internal readonly ConcurrentQueue<byte> Input = new();
        internal byte[] AfterPrompt = Array.Empty<byte>();
        internal string Written = "";
        internal bool Flushed, Disposed, WaitForInput;
        internal int Validations, Reads;
        internal Action<Access, int>? OnValidation;
        internal readonly TaskCompletionSource Prompted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<CancellationToken>? Activation;
        public void WaitForActive(CancellationToken token) { token.ThrowIfCancellationRequested(); Activation?.Invoke(token); }
        public void ValidateContext()
        { Validations++; OnValidation?.Invoke(this, Validations); ConsolePolicy.ValidateContext(Current); }
        public void FlushInput() { Flushed = true; Input.Clear(); }
        public void Write(byte[] bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Assert.True(Flushed);
            Written = Encoding.UTF8.GetString(bytes);
            foreach (byte value in AfterPrompt) Input.Enqueue(value);
            Prompted.TrySetResult();
        }
        public int ReadByte(CancellationToken token)
        {
            while (WaitForInput && Input.IsEmpty)
            { token.ThrowIfCancellationRequested(); token.WaitHandle.WaitOne(10); }
            token.ThrowIfCancellationRequested();
            int value = Input.TryDequeue(out byte next) ? next : -1;
            Reads++; return value;
        }
        public bool HasPendingInput() => !Input.IsEmpty;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task ActivationHandoffPrecedesUnchangedValidationFlushAndChallenge()
    {
        var fixture = await LinuxRawRestoreOperationTests.ConsoleReview(); await using var cleanup = fixture.Cleanup;
        var access = new Access { Current = Context with { ActiveVt = 2 }, AfterPrompt = Encoding.ASCII.GetBytes(ConsolePolicy.Challenge(fixture.Review) + "\n") };
        access.Activation = _ => { Assert.False(access.Flushed); Assert.Equal(0, access.Validations); access.Current = Context; };
        await new ConsolePolicy(Text, CultureInfo.InvariantCulture, () => access).ConfirmAsync(fixture.Review, CancellationToken.None);
        Assert.True(access.Disposed); Assert.Equal(4, access.Validations);
    }

    [Fact]
    public async Task CancelledActivationCannotFlushPromptOrApproveAndReleasesAccess()
    {
        var fixture = await LinuxRawRestoreOperationTests.ConsoleReview(); await using var cleanup = fixture.Cleanup;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var access = new Access { Current = Context with { ActiveVt = 2 } };
        access.Activation = token => { entered.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); };
        var run = new ConsolePolicy(Text, CultureInfo.InvariantCulture, () => access).ConfirmAsync(fixture.Review, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(access.Disposed); Assert.False(access.Flushed); Assert.Equal("", access.Written);
    }

    [Fact]
    public void ExactRootPrivateActiveControllingConsoleIsAccepted()
    {
        ConsolePolicy.ValidateContext(Context);
        // Device filesystem major/minor are unrelated to the fixed tty rdev.
        ConsolePolicy.ValidateContext(Context with
        { Held = Context.Held with { DeviceMinor = 8 }, Path = Context.Path with { DeviceMinor = 8 } });
    }

    public static IEnumerable<object[]> UnsafeContexts()
    {
        yield return new object[] { Context with { User = 65534 } };
        yield return new object[] { Context with { EffectiveUser = 65534 } };
        yield return new object[] { Context with { Group = 65534 } };
        yield return new object[] { Context with { EffectiveGroup = 65534 } };
        yield return new object[] { Context with { Held = Context.Held with { Mask = 0x1f } } };
        yield return new object[] { Context with { Path = Context.Path with { Mask = 0x1f } } };
        yield return new object[] { Context with { Held = Context.Held with { Mode = 0x21a0 } } }; // group write
        yield return new object[] { Context with { Held = Context.Held with { Mode = 0x8180 } } }; // file
        yield return new object[] { Context with { Path = Context.Path with { Mode = 0xa180 } } }; // link
        yield return new object[] { Context with { Held = Context.Held with { Owner = 65534 } } };
        yield return new object[] { Context with { Path = Context.Path with { Owner = 65534 } } };
        yield return new object[] { Context with { Held = Context.Held with { Links = 2 } } };
        yield return new object[] { Context with { Path = Context.Path with { Links = 2 } } };
        yield return new object[] { Context with { Held = Context.Held with { SpecialMajor = 136 } } }; // PTY
        yield return new object[] { Context with { Path = Context.Path with { SpecialMajor = 136 } } };
        yield return new object[] { Context with { Held = Context.Held with { SpecialMinor = 2 } } };
        yield return new object[] { Context with { Path = Context.Path with { SpecialMinor = 2 } } };
        yield return new object[] { Context with { Path = Context.Path with { Inode = 124 } } };
        yield return new object[] { Context with { Path = Context.Path with { DeviceMajor = 1 } } };
        yield return new object[] { Context with { Path = Context.Path with { DeviceMinor = 6 } } };
        yield return new object[] { Context with { IsTerminal = false } };
        yield return new object[] { Context with { TerminalSession = 43 } };
        yield return new object[] { Context with { ProcessSession = -1, TerminalSession = -1 } };
        yield return new object[] { Context with { ForegroundGroup = 43 } };
        yield return new object[] { Context with { ProcessGroup = -1, ForegroundGroup = -1 } };
        yield return new object[] { Context with { ActiveVt = 2 } };
        yield return new object[] { Context with { Canonical = false } };
        yield return new object[] { Context with { InjectionDisabled = false } };
    }

    [Theory]
    [MemberData(nameof(UnsafeContexts))]
    public void UnsafeIdentityTerminalReplacementSessionAndInjectionContextsRefuse(object value) =>
        Assert.Throws<InvalidDataException>(() => ConsolePolicy.ValidateContext((ConsolePolicy.Context)value));

    [Fact]
    public async Task ExactFreshInputFollowsFlushAndLocalizedActualReview()
    {
        var fixture = await LinuxRawRestoreOperationTests.ConsoleReview("serial:CONSOLE-42");
        await using var cleanup = fixture.Cleanup;
        var access = new Access { AfterPrompt = Encoding.ASCII.GetBytes(ConsolePolicy.Challenge(fixture.Review) + "\n") };
        var console = new ConsolePolicy(Text, CultureInfo.GetCultureInfo("it-IT"), () => access);
        await console.ConfirmAsync(fixture.Review, CancellationToken.None);
        Assert.True(access.Disposed); Assert.Equal(4, access.Validations);
        Assert.Contains("65.536", access.Written);
        Assert.Contains(fixture.Review.Image.ManifestRoot!, access.Written);
        Assert.Contains("Hardware identity: serial:CONSOLE-42", access.Written);
        Assert.DoesNotContain("\u001b", access.Written, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202e", access.Written, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => { _ = console.ConfirmAsync(fixture.Review, CancellationToken.None); });
    }

    [Theory]
    [InlineData("\u001b[31m\r\n\\secret\u202e", "\\u001B[31m\\u000D\\u000A\\u005Csecret\\u202E")]
    [InlineData("\0\t\u007f", "\\u0000\\u0009\\u007F")]
    [InlineData("serial:SAFE-42", "serial:SAFE-42")]
    public void TerminalControlsAndUnicodeFormattingAreEscapedAsTechnicalText(string input, string expected) =>
        Assert.Equal(expected, ConsolePolicy.Escape(input));

    [Theory]
    [InlineData("target")] [InlineData("review")] [InlineData("case")] [InlineData("oversize")]
    [InlineData("multiple")] [InlineData("control")] [InlineData("eof")] [InlineData("prequeued")]
    public async Task OnlyExactNewBoundedSingleLineInputCanConfirm(string fault)
    {
        var fixture = await LinuxRawRestoreOperationTests.ConsoleReview(); await using var cleanup = fixture.Cleanup;
        string expected = ConsolePolicy.Challenge(fixture.Review);
        string supplied = fault switch
        {
            "target" => "vdc " + fixture.Review.ReviewId.ToString("D"),
            "review" => "vdb " + Guid.NewGuid().ToString("D"),
            "case" => expected.ToUpperInvariant(),
            "oversize" => new string('x', 129),
            "multiple" => expected + "\nextra",
            "control" => expected + "\u001b",
            "eof" or "prequeued" => "",
            _ => throw new InvalidOperationException()
        };
        var access = new Access { AfterPrompt = Encoding.ASCII.GetBytes(supplied + (supplied.Length == 0 ? "" : "\n")) };
        if (fault == "prequeued") foreach (byte value in Encoding.ASCII.GetBytes(expected + "\n")) access.Input.Enqueue(value);
        var console = new ConsolePolicy(Text, CultureInfo.InvariantCulture, () => access);
        await Assert.ThrowsAsync<InvalidDataException>(() => console.ConfirmAsync(fixture.Review, CancellationToken.None));
        Assert.True(access.Flushed); Assert.True(access.Disposed);
    }

    [Theory]
    [InlineData(3)] [InlineData(4)]
    public async Task ContextIsRecheckedAfterInputAndImmediatelyBeforeApproval(int changedAt)
    {
        var fixture = await LinuxRawRestoreOperationTests.ConsoleReview(); await using var cleanup = fixture.Cleanup;
        var access = new Access { AfterPrompt = Encoding.ASCII.GetBytes(ConsolePolicy.Challenge(fixture.Review) + "\n") };
        access.OnValidation = (value, count) => { if (count == changedAt) value.Current = Context with { ActiveVt = 2 }; };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ConsolePolicy(Text, CultureInfo.InvariantCulture, () => access)
            .ConfirmAsync(fixture.Review, CancellationToken.None));
        Assert.True(access.Disposed);
    }

    [Fact]
    public async Task CancelledPollInputTerminatesBeforeReleasingItsOwnedAccess()
    {
        var fixture = await LinuxRawRestoreOperationTests.ConsoleReview(); await using var cleanup = fixture.Cleanup;
        var access = new Access { WaitForInput = true }; using var cancellation = new CancellationTokenSource();
        var task = new ConsolePolicy(Text, CultureInfo.InvariantCulture, () => access).ConfirmAsync(fixture.Review, cancellation.Token);
        await access.Prompted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(access.Disposed);
    }

    [Fact]
    public async Task CancellationDuringFinalContextCheckCannotCommitApproval()
    {
        var fixture = await LinuxRawRestoreOperationTests.ConsoleReview(); await using var cleanup = fixture.Cleanup;
        using var cancellation = new CancellationTokenSource();
        var access = new Access { AfterPrompt = Encoding.ASCII.GetBytes(ConsolePolicy.Challenge(fixture.Review) + "\n") };
        access.OnValidation = (_, count) => { if (count == 4) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConsolePolicy(Text, CultureInfo.InvariantCulture, () => access)
            .ConfirmAsync(fixture.Review, cancellation.Token));
        Assert.True(access.Disposed);
    }

    [Theory]
    [InlineData(null)] [InlineData("missing {0}")] [InlineData("bad \u001b{0} {1} {2}")]
    public async Task MissingOrMalformedTranslationNeverOpensConsole(string? invalid)
    {
        var fixture = await LinuxRawRestoreOperationTests.ConsoleReview(); await using var cleanup = fixture.Cleanup;
        int opens = 0;
        var console = new ConsolePolicy(key => key == "Recovery.Console.Target" ? invalid : Text(key), CultureInfo.InvariantCulture,
            () => { opens++; return new Access(); });
        Assert.Throws<InvalidDataException>(() => { _ = console.ConfirmAsync(fixture.Review, CancellationToken.None); });
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task NonLinuxProductionConsoleRefusesWithoutUsingStandardInput()
    {
        if (OperatingSystem.IsLinux()) return;
        var fixture = await LinuxRawRestoreOperationTests.ConsoleReview(); await using var cleanup = fixture.Cleanup;
        await Assert.ThrowsAsync<InvalidDataException>(() => new ConsolePolicy(Text, CultureInfo.InvariantCulture)
            .ConfirmAsync(fixture.Review, CancellationToken.None));
    }
}
