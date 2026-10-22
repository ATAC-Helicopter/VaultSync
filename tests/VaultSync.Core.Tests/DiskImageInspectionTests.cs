#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImageInspectionTests
{
    private static readonly Guid Operation=Guid.Parse("12345678-1234-5678-9abc-0123456789ab");
    private static DiskImageInspectionObservation Valid => new(DiskImageRawProfileVerificationState.ContentAndLayoutVerified,
        Guid.Parse(new string('1',32)),Guid.Parse(new string('2',32)),1,new string('a',64),65536,512,512,new string('b',64));

    private const string Status="Uid:\t65534\t65534\t65534\t65534\nGid:\t65534\t65534\t65534\t65534\nCapInh:\t0\nCapPrm:\t0\nCapEff:\t0\nCapAmb:\t0\nNoNewPrivs:\t1\nSeccomp:\t2\n";
    [Fact]
    public void MemoryBudgetsIncludeExistingRuntimeReservationsWithoutUnboundedGrowth()
    {
        var limits=LinuxImageInspectionSandbox.MemoryLimits(4*1024*1024,700*1024);
        Assert.Equal(6UL*1024*1024*1024,limits.Address);Assert.Equal(1212UL*1024*1024,limits.Data);
        Assert.Throws<InvalidDataException>(()=>LinuxImageInspectionSandbox.MemoryLimits(0,1));
        Assert.Throws<InvalidDataException>(()=>LinuxImageInspectionSandbox.MemoryLimits(ulong.MaxValue,1));
        Assert.Throws<InvalidDataException>(()=>LinuxImageInspectionSandbox.MemoryLimits(1,ulong.MaxValue));
        Assert.Throws<InvalidDataException>(()=>LinuxImageInspectionSandbox.MemoryLimits(1,1536*1024+1));
    }
    [Fact]
    public void ThreadRetirementRequiresANewCompleteStableSnapshot()
    {
        int lists=0,reads=0;
        LinuxImageInspectionSandbox.ValidateThreads(65534,65534,true,()=>++lists==1?["a","retired"]:["a"],task=>
        {reads++;return task=="retired"?throw new DirectoryNotFoundException():Status;});
        Assert.Equal(3,lists);Assert.Equal(3,reads);
    }
    [Fact]
    public void UnstableMissingOrUnreadableThreadsNeverBecomeAPartialPass()
    {
        int lists=0;
        Assert.Throws<InvalidDataException>(()=>LinuxImageInspectionSandbox.ValidateThreads(65534,65534,true,
            ()=>new[]{(++lists).ToString()},_=>Status)); Assert.Equal(6,lists);
        Assert.Throws<InvalidDataException>(()=>LinuxImageInspectionSandbox.ValidateThreads(65534,65534,true,
            ()=>["gone"],_=>throw new FileNotFoundException()));
        Assert.Throws<UnauthorizedAccessException>(()=>LinuxImageInspectionSandbox.ValidateThreads(65534,65534,true,
            ()=>["denied"],_=>throw new UnauthorizedAccessException()));
    }
    [Theory]
    [InlineData("Uid:\t65534", "Uid:\t0")][InlineData("Gid:\t65534", "Gid:\t0")]
    [InlineData("CapInh:\t0","CapInh:\t1")][InlineData("CapPrm:\t0","CapPrm:\t1")]
    [InlineData("CapEff:\t0","CapEff:\t1")][InlineData("CapAmb:\t0","CapAmb:\t1")]
    [InlineData("NoNewPrivs:\t1","NoNewPrivs:\t0")][InlineData("Seccomp:\t2","Seccomp:\t0")]
    public void EveryUnsafeThreadIdentityCapabilityOrPolicyRefuses(string before,string after) =>
        Assert.Throws<InvalidDataException>(()=>LinuxImageInspectionSandbox.ValidateThreads(65534,65534,true,()=>["a"],_=>Status.Replace(before,after)));

    [Fact]
    public async Task TrustedStartupCorpusIsFixedAndPassesWithoutCallerInput()
    {
        var assembly=typeof(DiskImageInspectionWorkerClient).Assembly;
        using var metadata=assembly.GetManifestResourceStream("VaultSync.Core.Resources.Inspection.warmup.metadata")!;
        using var payload=assembly.GetManifestResourceStream("VaultSync.Core.Resources.Inspection.warmup.raw")!;
        Assert.Equal("cb131e311daa9bf5d2a94c9a61d021cd82c8daf80a0b1caeb6824cb140f6f304",Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(metadata)));
        Assert.Equal("f86d4a863bf4dced92eaa4290324e71f96a215e9f38c7acd8054a7c1a6c0a663",Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)));
        await DiskImageInspectionWorkerService.WarmupAsync(CancellationToken.None);
    }

    [Fact]
    public void RequestAndReadMatchIndependentLiteralLittleEndianAndNetworkUuidBytes()
    {
        byte[] request=Convert.FromHexString("5653494d52455131010000000100000012345678123456789abc0123456789ab00040000000000000000010000000000");
        Assert.Equal(request,DiskImageInspectionProtocol.Request(Operation,1024,65536));
        Assert.Equal((Operation,1024L,65536L),DiskImageInspectionProtocol.DecodeRequest(request));
        byte[] read=Convert.FromHexString("5653494d5245414412345678123456789abc0123456789ab01000000000200000010000000000000");
        Assert.Equal(read,DiskImageInspectionProtocol.Read(Operation,1,4096,512));
        Assert.Equal((1u,4096UL,512),DiskImageInspectionProtocol.DecodeRead(read,Operation));
    }

    [Theory]
    [InlineData(0)][InlineData(8)][InlineData(12)][InlineData(16)]
    public void RequestRejectsWrongMagicVersionRoleAndEmptyOperation(int region)
    {
        byte[] request=DiskImageInspectionProtocol.Request(Operation,1024,65536);
        if(region==16) request.AsSpan(16,16).Clear(); else request[region]^=1;
        Assert.Throws<InvalidDataException>(()=>DiskImageInspectionProtocol.DecodeRequest(request));
    }

    [Theory]
    [InlineData(-1L,65536L)][InlineData(16777217L,65536L)][InlineData(1L,-1L)][InlineData(1L,8796093022209L)]
    public void RequestRefusesLengthPolicyViolations(long metadata,long payload) =>
        Assert.Throws<InvalidDataException>(()=>DiskImageInspectionProtocol.Request(Operation,metadata,payload));

    [Theory]
    [InlineData(512u)][InlineData(4096u)]
    public void FixedResultPreservesBoundFactsAndNoReasonStrings(uint sector)
    {
        var observation=Valid with {LogicalSectorBytes=sector,PhysicalSectorBytes=sector};
        byte[] bytes=DiskImageInspectionProtocol.Result(Operation,observation);
        Assert.Equal(152,bytes.Length); Assert.Equal(observation,DiskImageInspectionProtocol.DecodeResult(bytes,Operation,65536));
        Assert.Throws<InvalidDataException>(()=>DiskImageInspectionProtocol.DecodeResult(bytes,Guid.NewGuid(),65536));
        Assert.Throws<InvalidDataException>(()=>DiskImageInspectionProtocol.DecodeResult(bytes,Operation,131072));
    }

    [Theory]
    [InlineData(0)][InlineData(8)][InlineData(24)][InlineData(28)][InlineData(32)][InlineData(48)][InlineData(64)][InlineData(104)][InlineData(112)][InlineData(116)]
    public void InvalidResultFramingOrRequiredFactsNeverBecomeVerified(int region)
    {
        byte[] bytes=DiskImageInspectionProtocol.Result(Operation,Valid);
        if(region is 32 or 48) bytes.AsSpan(region,16).Clear();
        else if(region==64) bytes.AsSpan(region,8).Clear();
        else bytes[region]^=region==24?(byte)128:(byte)1;
        Assert.Throws<InvalidDataException>(()=>DiskImageInspectionProtocol.DecodeResult(bytes,Operation,65536));
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    public void RefusalsCannotCarryImageClaims(int state)
    {
        var refusal=new DiskImageInspectionObservation((DiskImageRawProfileVerificationState)state);
        byte[] bytes=DiskImageInspectionProtocol.Result(Operation,refusal);
        Assert.Equal(refusal,DiskImageInspectionProtocol.DecodeResult(bytes,Operation,65536));
        bytes[72]=1; Assert.Throws<InvalidDataException>(()=>DiskImageInspectionProtocol.DecodeResult(bytes,Operation,65536));
        Assert.Throws<InvalidDataException>(()=>DiskImageInspectionProtocol.Result(Operation,refusal with {VerifiedBytes=512}));
    }

    private static Task<DiskImageInspectionObservation> Serve(byte[] messages,MemoryStream output,
        Stream? metadata=null,Stream? payload=null,Guid? operation=null,CancellationToken token=default) =>
        DiskImageInspectionWorkerClient.ServeAsync(new MemoryStream(messages),output,
            new(metadata??new MemoryStream(new byte[16],false),payload??new MemoryStream(new byte[65536],false),operation??Operation,16,65536),()=>{},token);

    [Fact]
    public async Task OracleServesOnlyExactHeldStreamsWithoutClosingThem()
    {
        using var metadata=new MemoryStream(Enumerable.Range(0,16).Select(n=>(byte)n).ToArray(),false);
        using var payload=new MemoryStream(new byte[65536],false); using var output=new MemoryStream();
        byte[] messages=DiskImageInspectionProtocol.Read(Operation,0,4,8).Concat(DiskImageInspectionProtocol.Result(Operation,Valid)).ToArray();
        Assert.Equal(Valid,await Serve(messages,output,metadata,payload));
        byte[] reply=Convert.FromHexString("08000000000000000405060708090a0b"); Assert.Equal(reply,output.ToArray());
        Assert.True(metadata.CanRead); Assert.True(payload.CanRead);
    }

    [Theory]
    [InlineData(0u,16UL,1)][InlineData(1u,65535UL,2)][InlineData(1u,ulong.MaxValue,1)]
    public async Task OracleRefusesOutOfRangeWithoutAnyDataReply(uint source,ulong offset,int count)
    {
        using var output=new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(()=>Serve(DiskImageInspectionProtocol.Read(Operation,source,offset,count),output));
        Assert.Equal(0,output.Length);
    }

    [Fact]
    public async Task OracleRefusesCorrelationLengthChangesAndExcessReadBudget()
    {
        using var output=new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(()=>Serve(DiskImageInspectionProtocol.Read(Guid.NewGuid(),0,0,1),output));
        using var shortened=new MemoryStream(new byte[1],false);
        await Assert.ThrowsAsync<InvalidDataException>(()=>Serve(DiskImageInspectionProtocol.Read(Operation,0,0,1),output,shortened));
        byte[] repeated=Enumerable.Range(0,24).SelectMany(_=>DiskImageInspectionProtocol.Read(Operation,1,0,65536)).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(()=>Serve(repeated,output));
    }

    [Theory]
    [InlineData(0)][InlineData(7)][InlineData(20)][InlineData(39)]
    public async Task TruncatedMessagesNeverProduceAnObservation(int length)
    {
        using var output=new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(()=>Serve(DiskImageInspectionProtocol.Read(Operation,0,0,1)[..length],output));
    }

    [Fact]
    public void RemoteStreamCannotWriteSeekOutsideOrReadUnconfirmedReplies()
    {
        using var input=new MemoryStream(Convert.FromHexString("040000000000000001020304")); using var output=new MemoryStream();
        using var stream=new DiskImageInspectionWorkerService.RemoteStream(input,output,Operation,0,4,CancellationToken.None);
        byte[] bytes=new byte[8]; Assert.Equal(4,stream.Read(bytes)); Assert.Equal(new byte[]{1,2,3,4},bytes[..4]);
        Assert.Equal(0,stream.Read(bytes)); Assert.Throws<IOException>(()=>stream.Seek(5,SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(()=>stream.Write(bytes)); Assert.Throws<NotSupportedException>(()=>stream.SetLength(1));
        Assert.Equal(DiskImageInspectionProtocol.Read(Operation,0,0,4),output.ToArray());
    }

    [Theory]
    [InlineData("0000000000000000")][InlineData("0400000001000000")][InlineData("040000000000000001")]
    public void RemoteStreamRejectsShortWrongOrNoncanonicalReplies(string hex)
    {
        using var input=new MemoryStream(Convert.FromHexString(hex)); using var output=new MemoryStream();
        using var stream=new DiskImageInspectionWorkerService.RemoteStream(input,output,Operation,0,4,CancellationToken.None);
        var error=Record.Exception(()=>stream.Read(new byte[4])); Assert.True(error is IOException or InvalidDataException); Assert.Equal(0,stream.Position);
    }

    // Tiny independent classic-BPF interpreter for policy assertions. Native VM
    // tests separately prove installation/TSYNC and actual syscall refusals.
    private static uint Evaluate(uint number,ulong arg0=0,ulong arg1=0,ulong arg2=0,uint arch=0xc000003e)
    {
        byte[] data=new byte[64]; BinaryPrimitives.WriteUInt32LittleEndian(data,number); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4),arch);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16),arg0); BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(24),arg1); BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(32),arg2);
        var code=LinuxImageInspectionFilter.Build(1234,42); uint accumulator=0;
        for(int pc=0;pc<code.Length;pc++)
        {
            var op=code[pc];
            switch(op.Code)
            {
                case 0x20: accumulator=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)op.Value)); break;
                case 0x54: accumulator&=op.Value; break;
                case 0x15: pc+=accumulator==op.Value?op.True:op.False; break;
                case 6: return op.Value;
                default: throw new InvalidDataException("unknown-independent-bpf-opcode");
            }
        }
        throw new InvalidDataException("unterminated-bpf");
    }

    [Theory]
    [InlineData(2u)][InlineData(257u)][InlineData(437u)][InlineData(41u)][InlineData(42u)][InlineData(43u)][InlineData(53u)]
    [InlineData(59u)][InlineData(322u)][InlineData(57u)][InlineData(58u)][InlineData(101u)][InlineData(310u)][InlineData(311u)]
    [InlineData(272u)][InlineData(308u)][InlineData(321u)][InlineData(323u)][InlineData(425u)][InlineData(438u)][InlineData(450u)]
    [InlineData(105u)][InlineData(106u)][InlineData(117u)][InlineData(119u)][InlineData(126u)][InlineData(160u)][InlineData(165u)]
    [InlineData(0x40000000u)]
    public void FilterDeniesOpenNetworkExecForeignMemoryPrivilegeAndUnknownInterfaces(uint number) =>
        Assert.Equal(LinuxImageInspectionFilter.Deny,Evaluate(number));

    [Fact]
    public void FilterAllowsOnlyThreadClonesExactPeerAndSelfSignals()
    {
        Assert.Equal(8,Marshal.SizeOf<LinuxImageInspectionFilter.Instruction>());
        Assert.Equal(LinuxImageInspectionFilter.Kill,Evaluate(0,arch:0x40000003));
        Assert.Equal(0x50026u,Evaluate(435));
        Assert.Equal(LinuxImageInspectionFilter.Allow,Evaluate(56,0x3d0f00));
        foreach(ulong flags in new ulong[]{0,17,0x3d0f00|0x10000000,0x100000000|0x3d0f00}) Assert.Equal(LinuxImageInspectionFilter.Deny,Evaluate(56,flags));
        Assert.Equal(LinuxImageInspectionFilter.Allow,Evaluate(234,1234)); Assert.Equal(LinuxImageInspectionFilter.Deny,Evaluate(234,1235));
        Assert.Equal(LinuxImageInspectionFilter.Allow,Evaluate(44,42)); Assert.Equal(LinuxImageInspectionFilter.Deny,Evaluate(44,43));
        foreach(uint option in new uint[]{3,4,17}) Assert.Equal(LinuxImageInspectionFilter.Allow,Evaluate(55,42,1,option));
        Assert.Equal(LinuxImageInspectionFilter.Deny,Evaluate(55,42,1,77)); Assert.Equal(LinuxImageInspectionFilter.Deny,Evaluate(16,42,0x127b));
        Assert.Equal(LinuxImageInspectionFilter.Allow,Evaluate(16,42,0x541b));
        Assert.Equal(LinuxImageInspectionFilter.Deny,Evaluate(157,38,0)); Assert.Equal(LinuxImageInspectionFilter.Allow,Evaluate(157,15));
        Assert.Equal(LinuxImageInspectionFilter.Allow,Evaluate(203,0));
        Assert.Equal(LinuxImageInspectionFilter.Deny,Evaluate(203,1234));
        Assert.Equal(LinuxImageInspectionFilter.Deny,Evaluate(203,0x100000000));
    }
}
