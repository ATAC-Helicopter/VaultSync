using System.Buffers.Binary;
using System.Diagnostics;

namespace VaultSync.Core.Services;

internal enum DiskImageInspectionWorkerOutcome { Completed,Unavailable,Failed,TimedOut }
internal sealed record DiskImageInspectionWorkerResult(DiskImageInspectionWorkerOutcome Outcome,
    DiskImageInspectionObservation? Observation=null);

/// <summary>
/// Internal development inspector. Only bounded reads from two exclusively owned
/// caller-held read-only streams; no paths or handles cross the wire. Its summary
/// is not trusted executable provenance, a staged image or restore authorization.
/// </summary>
internal static class DiskImageInspectionWorkerClient
{
    internal static Task<DiskImageInspectionWorkerResult> InspectAsync(Stream metadata,Stream payload,CancellationToken token=default) =>
        InspectAsync(metadata,payload,Path.Combine(AppContext.BaseDirectory,"VaultSync.DiskWorker"),TimeSpan.FromSeconds(130),token);

    // Only local process fixtures may override the fixed adjacent installation.
    internal static async Task<DiskImageInspectionWorkerResult> InspectAsync(Stream metadata,Stream payload,
        string worker,TimeSpan timeout,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(!LinuxInventoryWorkerPeerChannel.Supported || !Path.IsPathFullyQualified(worker) || !File.Exists(worker) ||
            new FileInfo(worker).LinkTarget is not null) return new(DiskImageInspectionWorkerOutcome.Unavailable);
        if(ReferenceEquals(metadata,payload) || !metadata.CanRead || !payload.CanRead || !metadata.CanSeek || !payload.CanSeek ||
            metadata.CanWrite || payload.CanWrite) return new(DiskImageInspectionWorkerOutcome.Failed);
        long metadataLength=metadata.Length,payloadLength=payload.Length;
        Guid operation=Guid.NewGuid(); byte[] request;
        try { request=DiskImageInspectionProtocol.Request(operation,metadataLength,payloadLength); }
        catch(InvalidDataException) { return new(DiskImageInspectionWorkerOutcome.Failed); }
        uint user=LinuxImageInspectionSandbox.User,group=LinuxImageInspectionSandbox.Group;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        using var listener=new LinuxInventoryWorkerPeerChannel.Listener();
        using var process=new Process { StartInfo=new(worker) { UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(worker)! } };
        // No inherited loader hooks/profilers/home/network settings. The native
        // fixed adjacent apphost still needs installation/provenance qualification.
        process.StartInfo.Environment.Clear();
        process.StartInfo.Environment["LANG"]="C";
        process.StartInfo.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"]="1";
        process.StartInfo.Environment["DOTNET_EnableDiagnostics"]="0";
        process.StartInfo.Environment["DOTNET_GCHeapHardLimit"]="0x10000000";
        // Framework-dependent development tests use the already executing runtime,
        // never a runtime path supplied by an image or inherited loader setting.
        var runtime=new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        if(runtime.Parent?.Name=="Microsoft.NETCore.App" && runtime.Parent.Parent?.Name=="shared" && runtime.Parent.Parent.Parent is { } root)
            process.StartInfo.Environment["DOTNET_ROOT"]=root.FullName;
        process.StartInfo.ArgumentList.Add(DiskImageInspectionWorkerService.Argument); process.StartInfo.ArgumentList.Add(listener.Endpoint);
        bool started=false;
        try
        {
            started=process.Start(); if(!started) return new(DiskImageInspectionWorkerOutcome.Failed);
            using var peer=await DiskInventoryWorkerClient.AuthenticatePeerAsync(listener,process,deadline.Token).ConfigureAwait(false);
            Task quiet=DiskInventoryWorkerClient.RequireEmptyDiagnosticsAsync(process.StandardError.BaseStream,deadline.Token);
            Task<DiskImageInspectionObservation> response=ExchangeAsync(process,peer,metadata,payload,request,operation,
                metadataLength,payloadLength,user==0?LinuxImageInspectionSandbox.Nobody:user,
                user==0?LinuxImageInspectionSandbox.Nobody:group,deadline.Token);
            var pending=new List<Task> {response,quiet,process.WaitForExitAsync(deadline.Token)};
            _=Task.WhenAll(pending).ContinueWith(static t=>{_=t.Exception;},CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            while(pending.Count!=0)
            {
                Task completed=await Task.WhenAny(pending).WaitAsync(deadline.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false); pending.Remove(completed);
            }
            return process.ExitCode==0 ? new(DiskImageInspectionWorkerOutcome.Completed,await response.ConfigureAwait(false))
                : new(DiskImageInspectionWorkerOutcome.Failed);
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested) { return new(DiskImageInspectionWorkerOutcome.TimedOut); }
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException)
        { return new(DiskImageInspectionWorkerOutcome.Failed); }
        finally
        {
            deadline.Cancel();
            await StopAsync(process,started).ConfigureAwait(false);
        }
    }

    private static async Task StopAsync(Process process,bool started)
    {
        if(!started || process.HasExited) return;
        try { process.Kill(true); } catch(InvalidOperationException) { } catch(System.ComponentModel.Win32Exception) { }
        try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch(TimeoutException) { }
    }

    private static async Task<DiskImageInspectionObservation> ExchangeAsync(Process process,LinuxInventoryWorkerPeerChannel peer,
        Stream metadata,Stream payload,byte[] request,Guid operation,long metadataLength,long payloadLength,uint user,uint group,CancellationToken token)
    {
        await process.StandardInput.BaseStream.WriteAsync(request,token).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
        var observation=await ServeAsync(process.StandardOutput.BaseStream,process.StandardInput.BaseStream,
            metadata,payload,operation,metadataLength,payloadLength,peer.ValidatePeer,token).ConfigureAwait(false);
        peer.ValidatePeer(); LinuxImageInspectionSandbox.ValidateProcess(process.Id,user,group,true); peer.ValidatePeer();
        await peer.AcceptResponseAsync(token).ConfigureAwait(false);
        await DiskInventoryWorkerClient.RequireResponseEndAsync(process.StandardOutput.BaseStream,token).ConfigureAwait(false);
        return observation;
    }

    // Parent brokers only a bounded byte oracle for these two held streams. Never
    // service an image-selected path, native operation, descriptor or new stream.
    internal static async Task<DiskImageInspectionObservation> ServeAsync(Stream input,Stream output,Stream metadata,Stream payload,
        Guid operation,long metadataLength,long payloadLength,Action validatePeer,CancellationToken token)
    {
        ulong remaining=checked(3*(ulong)payloadLength+4*(ulong)metadataLength+1024*1024);
        ulong calls=checked(((ulong)payloadLength+(ulong)metadataLength)/4096*4+16384);
        byte[] prefix=new byte[8],frame=new byte[DiskImageInspectionProtocol.ReadBytes],buffer=new byte[DiskImageInspectionProtocol.ChunkBytes];
        byte[] header=new byte[8];
        while(true)
        {
            validatePeer(); await input.ReadExactlyAsync(prefix,token).ConfigureAwait(false);
            if(prefix.AsSpan().SequenceEqual("VSIMRSL1"u8))
            {
                byte[] result=new byte[DiskImageInspectionProtocol.ResultBytes]; prefix.CopyTo(result,0);
                await input.ReadExactlyAsync(result.AsMemory(8),token).ConfigureAwait(false); validatePeer();
                return DiskImageInspectionProtocol.DecodeResult(result,operation,payloadLength);
            }
            if(!prefix.AsSpan().SequenceEqual("VSIMREAD"u8) || calls--==0) throw new InvalidDataException("inspection.message-budget");
            prefix.CopyTo(frame,0); await input.ReadExactlyAsync(frame.AsMemory(8),token).ConfigureAwait(false);
            var read=DiskImageInspectionProtocol.DecodeRead(frame,operation);
            Stream source=read.Source==0?metadata:payload; long length=read.Source==0?metadataLength:payloadLength;
            if(!metadata.CanRead || !payload.CanRead || metadata.Length!=metadataLength || payload.Length!=payloadLength ||
                read.Offset>(ulong)length || (ulong)read.Count>(ulong)length-read.Offset || (ulong)read.Count>remaining)
                throw new InvalidDataException("inspection.read-budget");
            remaining-=(ulong)read.Count; source.Position=checked((long)read.Offset);
            await source.ReadExactlyAsync(buffer.AsMemory(0,read.Count),token).ConfigureAwait(false); validatePeer();
            BinaryPrimitives.WriteInt32LittleEndian(header,read.Count);
            await output.WriteAsync(header,token).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0,read.Count),token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false);
        }
    }
}
