#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RawImageRestoreJournalTests
{
    private sealed record Fixture(string HeaderHex, string[] RecordHex);
    private static Fixture Golden => JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,"Fixtures","disk-restore-journal-v1.json")),new JsonSerializerOptions {PropertyNameCaseInsensitive=true})!;
    private static readonly RawImageDeviceRestoreRequest Request = new(Guid.Parse(new string('1',32)),Guid.Parse(new string('2',32)),
        Guid.Parse(new string('3',32)),Guid.Parse(new string('4',32)),1,string.Concat(Enumerable.Repeat("ab",32)),
        new("vdb","254:16",null,LinuxBlockKind.WholeDisk,65536,512,512,2,false,"serial:FIXTURE",LinuxBlockIdentity.UniqueInSnapshot,false,false,false,LinuxBlockUse.Unknown));
    private static RawImageRestoreRecord Record(RawImageRestoreRecordKind kind) => new(kind,Request,0,
        kind is RawImageRestoreRecordKind.WriteIntent or RawImageRestoreRecordKind.RangeFlushed or RawImageRestoreRecordKind.ReadBackVerified?65536UL:0,
        kind is RawImageRestoreRecordKind.WriteIntent or RawImageRestoreRecordKind.RangeFlushed?string.Concat(Enumerable.Repeat("cd",32)):null);
    private static byte[] Header => Convert.FromHexString(Golden.HeaderHex);
    private static byte[] Records => Golden.RecordHex.SelectMany(Convert.FromHexString).ToArray();
    private static ValueTask<RawImageRestoreJournalAssessment> Inspect(byte[] header,byte[] records,RawImageDeviceRestoreRequest? expected=null) =>
        RawImageRestoreJournalCodec.InspectAsync(new MemoryStream(header),new MemoryStream(records),expected??Request);
    private static LinuxBlockInventory Inventory() => new(LinuxBlockInventoryState.Observed,LinuxBlockInventoryReason.NamespaceObservationOnly,
        Guid.NewGuid(),DateTimeOffset.UtcNow,Guid.Parse(new string('5',32)),"mnt:[1]",false,new[]{Request.Target,
        Request.Target with {KernelName="vda",DeviceNumber="254:0",HardwareIdentityCandidate="serial:OS",MountObserved=true,Use=LinuxBlockUse.ObservedInUse},
        Request.Target with {KernelName="vda1",DeviceNumber="254:1",Kind=LinuxBlockKind.Partition,ParentKernelName="vda"}});

    private sealed class Storage : RawImageRestoreJournal.IStorage
    {
        internal readonly MemoryStream Data=new(),Meta=new();
        internal readonly List<string> Steps=new();
        internal string? Fault;
        internal bool Disposed;
        public string? FilesystemDeviceNumber {get;set;}="254:1";
        public Stream Payload=>Data;
        public Stream Metadata=>Meta;
        public bool Published {get;private set;}
        private void Step(string step) {Steps.Add(step);if(Fault==step) throw new IOException("private-journal-path");}
        public void FlushPayload()=>Step("payload");
        public void FlushMetadata()=>Step("metadata");
        public void FlushDirectories()=>Step("directories");
        public void Publish() {Step("publish");Published=true;Step("parent-sync");}
        public ValueTask DisposeAsync(){Disposed=true;Data.Dispose();Meta.Dispose();return ValueTask.CompletedTask;}
    }
    private static ValueTask<RawImageRestoreJournal> Create(Storage storage,Func<CancellationToken,ValueTask<LinuxBlockInventory>>? observe=null)
    {
        var initial=Inventory();
        return RawImageRestoreJournal.CreateAsync(Request,initial,(reserve,allow)=>
        {Assert.Equal(640UL,reserve);if(!allow(storage.FilesystemDeviceNumber))throw new IOException("filesystem-refused");return storage;},
        observe??(_=>ValueTask.FromResult(initial)));
    }

    [Fact]
    public async Task IndependentPythonFixtureMatchesHeaderFramesAndReadBack()
    {
        Assert.Equal(Header,RawImageRestoreJournalCodec.Header(Request));
        var cursor=new RawImageRestoreJournalCodec.Cursor(Request,Header);
        foreach(var kind in new[]{RawImageRestoreRecordKind.Started,RawImageRestoreRecordKind.WriteIntent,RawImageRestoreRecordKind.RangeFlushed,RawImageRestoreRecordKind.ReadBackVerified})
        {byte[] frame=cursor.Encode(Record(kind));Assert.Equal(Convert.FromHexString(Golden.RecordHex[(int)kind]),frame);cursor.Accept(frame);}
        var result=await Inspect(Header,Records);Assert.Equal(RawImageRestoreJournalState.ReadBackVerified,result.State);Assert.Equal(65536UL,result.AcknowledgedBytes);
    }

    [Theory]
    [InlineData(0)][InlineData(8)][InlineData(12)][InlineData(16)][InlineData(32)][InlineData(48)][InlineData(64)]
    [InlineData(80)][InlineData(88)][InlineData(96)][InlineData(100)][InlineData(104)][InlineData(112)][InlineData(116)]
    [InlineData(120)][InlineData(152)][InlineData(184)][InlineData(224)][InlineData(255)]
    public async Task EveryHeaderBindingRegionIsChecked(int offset)
    {byte[] bytes=Header;bytes[offset]^=1;Assert.Equal(RawImageRestoreJournalState.Invalid,(await Inspect(bytes,Records)).State);}

    [Theory]
    [InlineData(0)][InlineData(4)][InlineData(12)][InlineData(16)][InlineData(24)][InlineData(32)][InlineData(64)][InlineData(96)][InlineData(127)]
    public async Task EveryFrameRegionRejectsTampering(int offset)
    {
        for(int frame=0;frame<4;frame++){byte[] bytes=Records;bytes[128*frame+offset]^=1;Assert.Equal(RawImageRestoreJournalState.Invalid,(await Inspect(Header,bytes)).State);}
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(127)][InlineData(128)][InlineData(129)][InlineData(255)]
    [InlineData(256)][InlineData(383)][InlineData(384)][InlineData(385)][InlineData(511)]
    public async Task TruncatedHistoryNeverBecomesVerified(int length)
    {Assert.Equal(RawImageRestoreJournalState.Incomplete,(await Inspect(Header,Records[..length])).State);}

    [Fact]
    public async Task ReorderDuplicateExtraHeaderAndTrailingFramesRefuse()
    {
        byte[][] frames=Golden.RecordHex.Select(Convert.FromHexString).ToArray();
        foreach(var bytes in new[]{frames[1].Concat(frames[0]).ToArray(),Records.Concat(frames[3]).ToArray(),Records.Concat(new byte[]{0}).ToArray()})
            Assert.NotEqual(RawImageRestoreJournalState.ReadBackVerified,(await Inspect(Header,bytes)).State);
        Assert.Equal(RawImageRestoreJournalState.Invalid,(await Inspect(Header.Concat(new byte[]{0}).ToArray(),Records)).State);
    }

    [Fact]
    public async Task CorrectChecksumsCannotPromoteInvalidTransitions()
    {
        byte[] frame=Convert.FromHexString(Golden.RecordHex[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12),3); // Verified without Started/intent/flush.
        SHA256.HashData(frame.AsSpan(0,96)).CopyTo(frame,96);
        Assert.Equal(RawImageRestoreJournalState.Invalid,(await Inspect(Header,frame)).State);
    }

    [Fact]
    public async Task DurableWriterMatchesIndependentBytesAndRejectsAppendAfterVerification()
    {
        var storage=new Storage();await using var journal=await Create(storage);
        foreach(var kind in new[]{RawImageRestoreRecordKind.Started,RawImageRestoreRecordKind.WriteIntent,RawImageRestoreRecordKind.RangeFlushed,RawImageRestoreRecordKind.ReadBackVerified})
            await journal.AppendDurableAsync(Record(kind),CancellationToken.None);
        Assert.Equal(Header,storage.Meta.ToArray());Assert.Equal(Records,storage.Data.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await journal.AppendDurableAsync(Record(RawImageRestoreRecordKind.Started),CancellationToken.None));
        Assert.Equal(5,storage.Steps.Count(s=>s=="payload"));
        Assert.True(storage.Steps.IndexOf("metadata")<storage.Steps.IndexOf("publish"));
    }

    [Fact]
    public async Task UnconfirmedAppendPoisonsTheWriterAndNeverClaimsATerminalRecord()
    {
        var storage=new Storage();await using var journal=await Create(storage);
        await journal.AppendDurableAsync(Record(RawImageRestoreRecordKind.Started),CancellationToken.None);
        storage.Fault="payload";
        await Assert.ThrowsAsync<IOException>(async()=>await journal.AppendDurableAsync(Record(RawImageRestoreRecordKind.WriteIntent),CancellationToken.None));
        storage.Fault=null;
        await Assert.ThrowsAsync<IOException>(async()=>await journal.AppendDurableAsync(new(RawImageRestoreRecordKind.Failed,Request,0,65536),CancellationToken.None));
        Assert.Equal(RawImageRestoreJournalState.Incomplete,(await Inspect(storage.Meta.ToArray(),storage.Data.ToArray())).State);
    }

    [Theory]
    [InlineData("metadata")][InlineData("payload")][InlineData("directories")][InlineData("publish")][InlineData("parent-sync")]
    public async Task FailedCreationReturnsNoJournalAndClosesOwnedStorage(string fault)
    {
        var storage=new Storage {Fault=fault};await Assert.ThrowsAsync<IOException>(async()=>await Create(storage));Assert.True(storage.Disposed);
        Assert.Equal(fault=="parent-sync",storage.Published);
    }

    [Theory]
    [InlineData(null)][InlineData("254:16")][InlineData("99:1")]
    public async Task UnknownOrTargetFilesystemRefusesBeforeCreation(string? number)
    {
        var storage=new Storage {FilesystemDeviceNumber=number};await Assert.ThrowsAsync<IOException>(async()=>await Create(storage));Assert.Empty(storage.Steps);
    }

    [Fact]
    public async Task ChangedIndependentInventoryRefusesBeforeAllocation()
    {
        int calls=0;var initial=Inventory();
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await RawImageRestoreJournal.CreateAsync(Request,initial,
            (_,_)=>{calls++;return new Storage();},_=>ValueTask.FromResult(initial with {BootId=Guid.NewGuid()})));
        Assert.Equal(0,calls);
    }

    [Fact]
    public async Task ChangedPostCreationTopologyClosesWithoutPublication()
    {
        int calls=0;var storage=new Storage();var initial=Inventory();
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await Create(storage,_=>ValueTask.FromResult(++calls==1?initial:initial with {BootId=Guid.NewGuid()})));
        Assert.True(storage.Disposed);Assert.False(storage.Published);
    }

    [Fact]
    public async Task FailureAfterVerificationOverridesTheByteObservationWithoutGrantingResume()
    {
        var storage=new Storage();await using var journal=await Create(storage);
        foreach(var kind in new[]{RawImageRestoreRecordKind.Started,RawImageRestoreRecordKind.WriteIntent,RawImageRestoreRecordKind.RangeFlushed,RawImageRestoreRecordKind.ReadBackVerified})
            await journal.AppendDurableAsync(Record(kind),CancellationToken.None);
        await journal.AppendDurableAsync(new(RawImageRestoreRecordKind.Failed,Request,0,0),CancellationToken.None);
        Assert.Equal(RawImageRestoreJournalState.Failed,(await Inspect(storage.Meta.ToArray(),storage.Data.ToArray())).State);
    }

    [Theory]
    [InlineData(4)][InlineData(5)]
    public async Task PendingIntentFailureRetainsItsExactUncertainRange(int kindValue)
    {
        var storage=new Storage();await using var journal=await Create(storage);
        await journal.AppendDurableAsync(Record(RawImageRestoreRecordKind.Started),CancellationToken.None);
        await journal.AppendDurableAsync(Record(RawImageRestoreRecordKind.WriteIntent),CancellationToken.None);
        await journal.AppendDurableAsync(new((RawImageRestoreRecordKind)kindValue,Request,0,65536),CancellationToken.None);
        var assessment=await Inspect(storage.Meta.ToArray(),storage.Data.ToArray());
        Assert.Equal(kindValue==4?RawImageRestoreJournalState.Interrupted:RawImageRestoreJournalState.Failed,assessment.State);
        Assert.Equal(0UL,assessment.AcknowledgedBytes);Assert.Equal(65536UL,assessment.UncertainLength);
    }

    [Fact]
    public async Task InvalidBindingAndWrongRangeRefuseWithoutAdvancingTheJournal()
    {
        var storage=new Storage();await using var journal=await Create(storage);
        await journal.AppendDurableAsync(Record(RawImageRestoreRecordKind.Started),CancellationToken.None);
        foreach(var record in new[]{Record(RawImageRestoreRecordKind.WriteIntent) with {Offset=512},
            Record(RawImageRestoreRecordKind.WriteIntent) with {Length=512},
            Record(RawImageRestoreRecordKind.WriteIntent) with {Sha256=new string('G',64)},
            Record(RawImageRestoreRecordKind.WriteIntent) with {Request=Request with {OperationId=Guid.NewGuid()}},
            Record(RawImageRestoreRecordKind.RangeFlushed),Record(RawImageRestoreRecordKind.ReadBackVerified)})
            await Assert.ThrowsAsync<InvalidDataException>(async()=>await journal.AppendDurableAsync(record,CancellationToken.None));
        Assert.Equal(128,storage.Data.Length);
        await journal.AppendDurableAsync(Record(RawImageRestoreRecordKind.WriteIntent),CancellationToken.None);
    }

    [Fact]
    public async Task ChangedStreamPositionPoisonsTheWriter()
    {
        var storage=new Storage();await using var journal=await Create(storage);storage.Data.Position=1;
        await Assert.ThrowsAsync<IOException>(async()=>await journal.AppendDurableAsync(Record(RawImageRestoreRecordKind.Started),CancellationToken.None));
        storage.Data.Position=0;
        await Assert.ThrowsAsync<IOException>(async()=>await journal.AppendDurableAsync(Record(RawImageRestoreRecordKind.Started),CancellationToken.None));
        Assert.Equal(0,storage.Data.Length);
    }

    [Fact]
    public async Task ConcurrentStartupAndDisposedWriterCannotAppendExtraFrames()
    {
        var storage=new Storage();var journal=await Create(storage);
        async Task<bool> Append() {try{await journal.AppendDurableAsync(Record(RawImageRestoreRecordKind.Started),CancellationToken.None);return true;}
            catch(InvalidDataException){return false;}}
        var outcomes=await Task.WhenAll(Append(),Append());Assert.Single(outcomes,b=>b);Assert.Equal(128,storage.Data.Length);
        await journal.DisposeAsync();await journal.DisposeAsync();Assert.True(storage.Disposed);
        await Assert.ThrowsAsync<IOException>(async()=>await journal.AppendDurableAsync(Record(RawImageRestoreRecordKind.WriteIntent),CancellationToken.None));
    }

    [Theory]
    [InlineData("name")][InlineData("number")][InlineData("hardware")][InlineData("unicode")][InlineData("readonly")]
    [InlineData("sector")][InlineData("capacity")][InlineData("sequence")][InlineData("parent")][InlineData("root")]
    public void InvalidRequestHeaderRefusesBoundedInvalidClaims(string change)
    {
        var r=change=="root"?Request with {ManifestRoot="invalid"}:Request with {Target=change switch
        {
            "name"=>Request.Target with {KernelName="../vdb"},"number"=>Request.Target with {DeviceNumber="0254:16"},
            "hardware"=>Request.Target with {HardwareIdentityCandidate=new string('x',264)},
            "unicode"=>Request.Target with {HardwareIdentityCandidate="serial:\ud800"},
            "readonly"=>Request.Target with {IsReadOnly=true},"sector"=>Request.Target with {LogicalSectorBytes=0},
            "capacity"=>Request.Target with {Capacity=ulong.MaxValue},"sequence"=>Request.Target with {DiskSequence=null},
            _=>Request.Target with {ParentKernelName="vda"}
        }};
        Assert.Throws<InvalidDataException>(()=>RawImageRestoreJournalCodec.Header(r));
    }

    [Fact]
    public async Task ChangedJournalDiskIdentityRefusesBeforePublication()
    {
        int calls=0;var storage=new Storage();var initial=Inventory();
        var changed=initial with {Devices=initial.Devices.Select(d=>d.KernelName=="vda"?d with {DiskSequence=99}:d).ToArray()};
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await Create(storage,_=>ValueTask.FromResult(++calls==1?initial:changed)));
        Assert.True(storage.Disposed);Assert.False(storage.Published);
    }

    [Fact]
    public async Task InitiallySeekedStorageRefusesWithoutWriting()
    {
        var storage=new Storage();storage.Data.Position=1;
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await Create(storage));
        Assert.True(storage.Disposed);Assert.Empty(storage.Steps);
    }

    [Fact]
    public async Task InspectorRejectsAliasingSeekedInputAndHonorsCancellation()
    {
        using var shared=new MemoryStream(Header);
        Assert.Equal(RawImageRestoreJournalState.Invalid,(await RawImageRestoreJournalCodec.InspectAsync(shared,shared,Request)).State);
        using var header=new MemoryStream(Header);using var records=new MemoryStream(Records);records.Position=128;
        Assert.Equal(RawImageRestoreJournalState.Invalid,(await RawImageRestoreJournalCodec.InspectAsync(header,records,Request)).State);
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await RawImageRestoreJournalCodec.InspectAsync(header,records,Request,canceled.Token));
    }
}
