using System.Buffers.Binary;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

internal sealed record DiskImageInspectionObservation(DiskImageRawProfileVerificationState State,
    Guid ImageId=default,Guid CaptureId=default,ulong Generation=0,string? ManifestRoot=null,
    ulong VerifiedBytes=0,uint LogicalSectorBytes=0,uint PhysicalSectorBytes=0,string? PayloadSha256=null);

// Fixed binary records only; no reason text, path, device selector or content echo.
internal static class DiskImageInspectionProtocol
{
    internal const int RequestBytes=48,ReadBytes=40,ResultBytes=152,ChunkBytes=128*1024;
    internal static byte[] Request(Guid operation,long metadata,long payload)
    {
        if(operation==Guid.Empty || metadata<0 || metadata>DiskImageRawProfileVerificationService.MaximumMetadataBytes ||
            payload<0 || (ulong)payload>DiskImageRawProfileVerificationService.MaximumCapacity)
            throw new InvalidDataException("inspection.request");
        byte[] bytes=new byte[RequestBytes]; "VSIMREQ1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12),1);
        operation.TryWriteBytes(bytes.AsSpan(16,16),true,out _);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32),metadata);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40),payload); return bytes;
    }
    internal static (Guid Operation,long Metadata,long Payload) DecodeRequest(byte[] bytes)
    {
        if(bytes.Length!=RequestBytes) throw new InvalidDataException("inspection.request-length");
        Guid operation=new(bytes.AsSpan(16,16),true);
        long metadata=BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32)),payload=BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40));
        if(!Request(operation,metadata,payload).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("inspection.request-frame");
        return(operation,metadata,payload);
    }
    internal static byte[] Read(Guid operation,uint source,ulong offset,int count)
    {
        if(operation==Guid.Empty || source>1 || count is <=0 or >ChunkBytes) throw new InvalidDataException("inspection.read");
        byte[] bytes=new byte[ReadBytes]; "VSIMREAD"u8.CopyTo(bytes); operation.TryWriteBytes(bytes.AsSpan(8,16),true,out _);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24),source); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28),count);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32),offset); return bytes;
    }
    internal static (uint Source,ulong Offset,int Count) DecodeRead(byte[] bytes,Guid operation)
    {
        if(bytes.Length!=ReadBytes || new Guid(bytes.AsSpan(8,16),true)!=operation) throw new InvalidDataException("inspection.read-operation");
        uint source=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)); int count=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28));
        ulong offset=BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32));
        if(!Read(operation,source,offset,count).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("inspection.read-frame");
        return(source,offset,count);
    }
    internal static DiskImageInspectionObservation Observe(DiskImageRawProfileVerificationAssessment assessment,string? payloadSha256=null)
    {
        if(assessment.State!=DiskImageRawProfileVerificationState.ContentAndLayoutVerified) return new(assessment.State);
        var image=assessment.Image ?? throw new InvalidDataException("inspection.missing-image");
        var origin=image.DeclaredOrigin ?? throw new InvalidDataException("inspection.missing-origin");
        return new(assessment.State,image.ImageId,image.CaptureId,image.Generation,image.ManifestRoot,image.VerifiedBytes,
            origin.Layout.LogicalSectorBytes,origin.PhysicalSectorBytes,payloadSha256);
    }
    internal static byte[] Result(Guid operation,DiskImageInspectionObservation observation)
    {
        if(operation==Guid.Empty || !Enum.IsDefined(observation.State)) throw new InvalidDataException("inspection.result-state");
        byte[] bytes=new byte[ResultBytes]; "VSIMRSL1"u8.CopyTo(bytes); operation.TryWriteBytes(bytes.AsSpan(8,16),true,out _);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24),(uint)observation.State);
        // Policy marker, not an authorization token. Parent independently checks /proc.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28),1);
        if(observation.State==DiskImageRawProfileVerificationState.ContentAndLayoutVerified)
        {
            if(observation.ImageId==Guid.Empty || observation.CaptureId==Guid.Empty || observation.Generation==0 ||
                !RawImageRestoreJournalCodec.Hash(observation.ManifestRoot) || !RawImageRestoreJournalCodec.Hash(observation.PayloadSha256) || observation.VerifiedBytes is 0 or >DiskImageRawProfileVerificationService.MaximumCapacity ||
                observation.LogicalSectorBytes is not (512 or 4096) || observation.PhysicalSectorBytes is not (512 or 4096) ||
                observation.PhysicalSectorBytes<observation.LogicalSectorBytes || observation.VerifiedBytes%observation.LogicalSectorBytes!=0)
                throw new InvalidDataException("inspection.result-binding");
            observation.ImageId.TryWriteBytes(bytes.AsSpan(32,16),true,out _); observation.CaptureId.TryWriteBytes(bytes.AsSpan(48,16),true,out _);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64),observation.Generation);
            Convert.FromHexString(observation.ManifestRoot!).CopyTo(bytes,72);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(104),observation.VerifiedBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(112),observation.LogicalSectorBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(116),observation.PhysicalSectorBytes);
            Convert.FromHexString(observation.PayloadSha256!).CopyTo(bytes,120);
        }
        else if(observation!=new DiskImageInspectionObservation(observation.State)) throw new InvalidDataException("inspection.refusal-facts");
        return bytes;
    }
    internal static DiskImageInspectionObservation DecodeResult(byte[] bytes,Guid operation,long payloadLength)
    {
        if(bytes.Length!=ResultBytes || new Guid(bytes.AsSpan(8,16),true)!=operation) throw new InvalidDataException("inspection.result-operation");
        var state=(DiskImageRawProfileVerificationState)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
        var result=state==DiskImageRawProfileVerificationState.ContentAndLayoutVerified
            ? new(state,new(bytes.AsSpan(32,16),true),new(bytes.AsSpan(48,16),true),BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(64)),
                Convert.ToHexStringLower(bytes.AsSpan(72,32)),BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(104)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(112)),BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(116)),Convert.ToHexStringLower(bytes.AsSpan(120,32)))
            : new DiskImageInspectionObservation(state);
        if(!Result(operation,result).AsSpan().SequenceEqual(bytes) ||
            result.State==DiskImageRawProfileVerificationState.ContentAndLayoutVerified && result.VerifiedBytes!=(ulong)payloadLength)
            throw new InvalidDataException("inspection.result-frame");
        return result;
    }
}
