#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskInventoryWorkerClientTests
{
    private static readonly Guid Operation = Guid.Parse("12345678-1234-5678-9abc-0123456789ab");
    private const string FailureBody = """
        {"State":1,"Reason":1,"ObservationId":"00000000-0000-0000-0000-000000000000",
        "ObservedAtUtc":"2026-10-20T00:00:00Z","BootId":"00000000-0000-0000-0000-000000000000",
        "MountNamespace":null,"HasUnresolvedSwaps":false,"Devices":[]}
        """;
    private const string ObservedBody = """
        {"State":0,"Reason":0,"ObservationId":"12345678-1234-5678-9abc-0123456789ab",
        "ObservedAtUtc":"2026-10-20T00:00:00Z","BootId":"12345678-1234-5678-9abc-0123456789ac",
        "MountNamespace":"mnt:[1234]","HasUnresolvedSwaps":false,"Devices":[
        {"KernelName":"vda","DeviceNumber":"254:0","ParentKernelName":null,"Kind":0,
        "Capacity":1048576,"LogicalSectorBytes":512,"PhysicalSectorBytes":512,"DiskSequence":1,
        "IsReadOnly":false,"HardwareIdentityCandidate":"serial:PUBLIC-FIXTURE","Identity":1,
        "MountObserved":false,"SwapObserved":false,"HasHolders":false,"Use":0}]}
        """;

    private static byte[] Frame(string json = FailureBody)
    {
        // Literal framing independent of the product response encoder.
        byte[] header = Convert.FromHexString("5653494e56525350010000000100000012345678123456789abc0123456789ab00000000");
        byte[] body = Encoding.UTF8.GetBytes(json);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), (uint)body.Length);
        return header.Concat(body).ToArray();
    }

    private static Task<LinuxBlockInventory> Read(byte[] bytes) =>
        DiskInventoryWorkerClient.ReadResponseAsync(new MemoryStream(bytes), Operation, CancellationToken.None);

    [Fact]
    public async Task RequestMatchesIndependentGoldenBytesAndLeavesPipeOpen()
    {
        using var output = new MemoryStream();
        await DiskInventoryWorkerClient.WriteRequestAsync(output, Operation, CancellationToken.None);
        Assert.Equal(Convert.FromHexString("5653494e56524551010000000100000012345678123456789abc0123456789ab"), output.ToArray());
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task ValidObservedAndUnsupportedResultsKeepTheirMeaning()
    {
        Assert.Equal(LinuxBlockInventoryState.UnsupportedPlatform, (await Read(Frame())).State);
        LinuxBlockInventory result = await Read(Frame(ObservedBody));
        Assert.Equal(LinuxBlockInventoryState.Observed, result.State);
        // Unknown remains unknown: receiving observations cannot authorize a write.
        Assert.Equal(LinuxBlockUse.Unknown, Assert.Single(result.Devices).Use);
    }

    public static IEnumerable<object[]> Truncations => Enumerable.Range(0, 36)
        .Concat(new[] { 36, 37, Frame().Length - 1 }).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Truncations))]
    public async Task EveryTruncatedFrameRefuses(int length) =>
        await Assert.ThrowsAsync<EndOfStreamException>(() => Read(Frame()[..length]));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 0)]
    [InlineData(11, 1)]
    [InlineData(12, 2)]
    [InlineData(15, 1)]
    [InlineData(16, 0)]
    public async Task WrongMagicVersionOperationAndCorrelationRefuse(int index, byte value)
    {
        byte[] frame = Frame(); frame[index] = value;
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(frame));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1048577u)]
    [InlineData(uint.MaxValue)]
    public async Task InvalidBodyLengthRefusesBeforeBodyRead(uint length)
    {
        byte[] frame = Frame()[..36];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(32), length);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(frame));
    }

    [Fact]
    public async Task ExtraByteOrSecondFrameRefuses()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(Frame().Concat(new byte[1]).ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(Frame().Concat(Frame()).ToArray()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"State\":0,\"State\":1}")]
    [InlineData("{\"Surprise\":true}")]
    [InlineData("[]")]
    [InlineData("{\"State\":\"Observed\"}")]
    public async Task InvalidJsonShapeRefuses(string body) =>
        await AssertInvalid(body);

    public static IEnumerable<object[]> InvalidObservations => new (string From, string To)[]
    {
        ("\"State\":0", "\"State\":999"),
        ("\"Reason\":0", "\"Reason\":999"),
        ("\"Devices\":[", "\"Devices\":null,\"Ignored\":["),
        ("mnt:[1234]", "other:[1234]"),
        ("\"Kind\":0", "\"Kind\":999"),
        ("\"Identity\":1", "\"Identity\":999"),
        ("\"Use\":0", "\"Use\":999"),
        ("\"KernelName\":\"vda\"", "\"KernelName\":\"../vda\""),
        ("\"DeviceNumber\":\"254:0\"", "\"DeviceNumber\":\"0254:0\""),
        ("\"ParentKernelName\":null", "\"ParentKernelName\":\"vda\""),
        ("\"ParentKernelName\":null", "\"ParentKernelName\":\"missing\""),
        ("\"LogicalSectorBytes\":512", "\"LogicalSectorBytes\":0"),
        ("\"LogicalSectorBytes\":512", "\"LogicalSectorBytes\":513"),
        ("\"PhysicalSectorBytes\":512", "\"PhysicalSectorBytes\":0"),
        ("\"PhysicalSectorBytes\":512", "\"PhysicalSectorBytes\":1025"),
        ("\"Capacity\":1048576", "\"Capacity\":1048575"),
        ("\"DiskSequence\":1", "\"DiskSequence\":0"),
        ("\"MountObserved\":false", "\"MountObserved\":true"),
        ("\"SwapObserved\":false", "\"SwapObserved\":true"),
        ("\"HasHolders\":false", "\"HasHolders\":true"),
        ("serial:PUBLIC-FIXTURE", "serial:\\nPRIVATE"),
        ("\"State\":0", "\"State\":1"),
    }.Select(p => new object[] { ObservedBody.Replace(p.From, p.To, StringComparison.Ordinal) });

    [Theory]
    [MemberData(nameof(InvalidObservations))]
    public async Task InvalidObservationCannotBecomeUsable(string json) =>
        await AssertInvalid(json);

    private static async Task AssertInvalid(string json)
    {
        Exception? exception = await Record.ExceptionAsync(() => Read(Frame(json)));
        Assert.True(exception is JsonException or InvalidDataException, exception?.GetType().FullName ?? "Accepted invalid response");
    }

    [Fact]
    public async Task DuplicateAndOversizedInventoriesRefuse()
    {
        using JsonDocument json = JsonDocument.Parse(ObservedBody);
        string device = json.RootElement.GetProperty("Devices")[0].GetRawText();
        foreach (int count in new[] { 2, 257 })
        {
            string body = ObservedBody[..ObservedBody.IndexOf("\"Devices\":", StringComparison.Ordinal)] +
                "\"Devices\":[" + string.Join(",", Enumerable.Repeat(device, count)) + "]}";
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(Frame(body)));
        }
    }

    [Fact]
    public async Task ExistingWorkerEncoderIsCompatibleWithStrictDecoder()
    {
        using var request = new MemoryStream();
        await DiskInventoryWorkerClient.WriteRequestAsync(request, Operation, CancellationToken.None);
        request.Position = 0;
        using var response = new MemoryStream();
        LinuxBlockInventory snapshot = JsonSerializer.Deserialize<LinuxBlockInventory>(ObservedBody)!;
        Assert.True(await DiskInventoryWorkerService.RunAsync(request, response, _ => ValueTask.FromResult(snapshot)));
        response.Position = 0;
        Assert.Single((await DiskInventoryWorkerClient.ReadResponseAsync(response, Operation, CancellationToken.None)).Devices);
    }

    [Fact]
    public async Task MissingWorkerAndCallerCancellationNeverLaunch()
    {
        var result = await DiskInventoryWorkerClient.ObserveAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), TimeSpan.FromSeconds(1));
        Assert.Equal(DiskInventoryWorkerOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Inventory);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskInventoryWorkerClient.ObserveAsync(canceled.Token));
    }

    [Fact]
    public async Task ActualAdjacentWorkerCompletesWithoutShellArgumentsOrPrivilege()
    {
        string name = OperatingSystem.IsWindows() ? "VaultSync.DiskWorker.exe" : "VaultSync.DiskWorker";
        DiskInventoryWorkerResult result = await DiskInventoryWorkerClient.ObserveAsync(
            Path.Combine(AppContext.BaseDirectory, name), TimeSpan.FromSeconds(35));
        Assert.Equal(DiskInventoryWorkerOutcome.Completed, result.Outcome);
        Assert.NotNull(result.Inventory);
        if (!OperatingSystem.IsLinux())
            Assert.Equal(LinuxBlockInventoryState.UnsupportedPlatform, result.Inventory.State);
    }

    [Fact]
    public async Task PublicLinuxClientRefusesUnstagedAdjacentDevelopmentWorker()
    {
        if (!LinuxInventoryWorkerPeerChannel.Supported) return;
        // The test output contains only the adjacent transport fixture, not the
        // packaged nested closure. It must never be a production fallback.
        Assert.False(Directory.Exists(Path.GetDirectoryName(LinuxWorkerInstallation.FixedWorker)));
        var result = await DiskInventoryWorkerClient.ObserveAsync();
        Assert.Equal(DiskInventoryWorkerOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Inventory);
    }

    [Fact]
    public async Task CompleteJsonWithDuplicateOrUnknownPropertiesIsRejected()
    {
        await AssertInvalid(FailureBody.Replace("\"State\":1", "\"State\":1,\"State\":0", StringComparison.Ordinal));
        await AssertInvalid(FailureBody.Replace("\"State\":1", "\"State\":1,\"Unknown\":true", StringComparison.Ordinal));
        await AssertInvalid(FailureBody.Replace("\"Devices\":[]", "\"Devices\":null", StringComparison.Ordinal));
    }
}
