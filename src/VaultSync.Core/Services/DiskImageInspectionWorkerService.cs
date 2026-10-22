using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VaultSync.Core.Services;

internal static class DiskImageInspectionWorkerService
{
    internal const string Argument="--inspect-peer";

    internal static async Task<bool> RunAsync(Stream input,Stream output,LinuxInventoryWorkerPeerChannel peer,CancellationToken token)
    {
        byte[] request=new byte[DiskImageInspectionProtocol.RequestBytes];
        await input.ReadExactlyAsync(request,token).ConfigureAwait(false);
        var facts=DiskImageInspectionProtocol.DecodeRequest(request);
        // Warm trusted runtime/crypto and pipe paths BEFORE the irreversible policy;
        // no image bytes are requested or parsed before entering it.
        _=SHA256.HashData(new byte[1]);
        await Task.Run(static () => _=SHA256.HashData(new byte[1]),token).ConfigureAwait(false);
        await WarmupAsync(token).ConfigureAwait(false);
        LinuxImageInspectionSandbox.Enter(peer.SocketDescriptor);
        using var metadata=new RemoteStream(input,output,facts.Operation,0,facts.Metadata,token);
        using var payload=new RemoteStream(input,output,facts.Operation,1,facts.Payload,token);
        var result=await DiskImageRawProfileVerificationService.VerifyAsync(metadata,payload,token).ConfigureAwait(false);
        string? hash=null;
        if(result.State==DiskImageRawProfileVerificationState.ContentAndLayoutVerified)
        { payload.Position=0; hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(payload,token).ConfigureAwait(false)); }
        byte[] response=DiskImageInspectionProtocol.Result(facts.Operation,DiskImageInspectionProtocol.Observe(result,hash));
        await output.WriteAsync(response,token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false);
        return true;
    }

    // Immutable module-owned synthetic bytes, never request/image-derived. Exercise
    // the managed/native parser dependencies before filesystem opens are forbidden.
    internal static async Task WarmupAsync(CancellationToken token)
    {
        var assembly=typeof(DiskImageInspectionWorkerService).Assembly;
        using var metadata=assembly.GetManifestResourceStream("VaultSync.Core.Resources.Inspection.warmup.metadata")
            ?? throw new InvalidDataException("inspection.missing-self-test");
        using var source=assembly.GetManifestResourceStream("VaultSync.Core.Resources.Inspection.warmup.raw")
            ?? throw new InvalidDataException("inspection.missing-self-test");
        using var payload=new MemoryStream(); await source.CopyToAsync(payload,token).ConfigureAwait(false); payload.Position=0;
        var result=await DiskImageRawProfileVerificationService.VerifyAsync(metadata,payload,token).ConfigureAwait(false);
        if(result.State!=DiskImageRawProfileVerificationState.ContentAndLayoutVerified || result.Image?.VerifiedBytes!=65536)
            throw new InvalidDataException("inspection.failed-self-test");
        payload.Position=0; _=await SHA256.HashDataAsync(payload,token).ConfigureAwait(false);
    }

    internal sealed class RemoteStream(Stream input,Stream output,Guid operation,uint source,long length,CancellationToken deadline) : Stream
    {
        private long position;
        private bool disposed;
        public override bool CanRead=>!disposed;
        public override bool CanSeek=>!disposed;
        public override bool CanWrite=>false;
        public override long Length=>length;
        public override long Position { get=>position; set=>Seek(value,SeekOrigin.Begin); }
        public override int Read(byte[] buffer,int offset,int count)
        { ValidateBufferArguments(buffer,offset,count); return Read(buffer.AsSpan(offset,count)); }
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed,this); deadline.ThrowIfCancellationRequested();
            int count=(int)Math.Min(Math.Min(buffer.Length,DiskImageInspectionProtocol.ChunkBytes),length-position);
            if(count==0) return 0;
            output.Write(DiskImageInspectionProtocol.Read(operation,source,(ulong)position,count)); output.Flush();
            Span<byte> header=stackalloc byte[8]; input.ReadExactly(header);
            if(BinaryPrimitives.ReadInt32LittleEndian(header)!=count || BinaryPrimitives.ReadUInt32LittleEndian(header[4..])!=0)
                throw new InvalidDataException("inspection.read-reply");
            input.ReadExactly(buffer[..count]); position+=count; deadline.ThrowIfCancellationRequested(); return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer,offset,count)); }
        public override long Seek(long offset,SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            long basis=origin switch {SeekOrigin.Begin=>0,SeekOrigin.Current=>position,SeekOrigin.End=>length,_=>throw new ArgumentOutOfRangeException(nameof(origin))};
            long next=checked(basis+offset); if(next<0 || next>length) throw new IOException("inspection.seek-range"); return position=next;
        }
        protected override void Dispose(bool disposing) { disposed=true; base.Dispose(disposing); }
        public override void Flush() { }
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
