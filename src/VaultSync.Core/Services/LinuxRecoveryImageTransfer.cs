using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VaultSync.Core.Services;

/// <summary>
/// Internal recovery transport primitive. Passes two already-open read-only regular
/// files across one authenticated Unix connection without letting root open UI paths.
/// This does not authenticate a build/role, authorize an operation or permit writes.
/// The owning protocol must serialize this phase with every other socket reader.
/// </summary>
internal static class LinuxRecoveryImageTransfer
{
    private const int ControlBytes = 256, HeaderBytes = 16, MessageMarker = 0x56;
    private const int DontWait = 0x40, CloseOnExec = 0x40000000, NoSignal = 0x4000;
    internal sealed class Sources(FileStream metadata, FileStream payload) : IDisposable
    {
        internal FileStream Metadata { get; } = metadata;
        internal FileStream Payload { get; } = payload;
        public void Dispose() { try { Metadata.Dispose(); } finally { Payload.Dispose(); } }
    }

    internal static async Task SendAsync(Socket socket, LinuxWorkerPeerBinding peer, FileStream metadata,
        FileStream payload, CancellationToken token = default)
    {
        RequirePlatform();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        CheckPeer(socket, peer, deadline.Token);
        if (metadata.CanWrite || payload.CanWrite) throw Refused();
        // Keep the exact SafeHandle objects across awaits. Re-reading a disposed
        // FileStream.SafeFileHandle during cleanup can itself throw and strand
        // the dangerous references that protect an in-flight send.
        var metadataHandle = metadata.SafeFileHandle;
        var payloadHandle = payload.SafeFileHandle;
        var first = CheckFile(metadataHandle, DiskImageRawProfileVerificationService.MaximumMetadataBytes);
        var second = CheckFile(payloadHandle, DiskImageRawProfileVerificationService.MaximumCapacity);
        if (SameFile(first, second)) throw Refused();
        bool firstHeld = false, secondHeld = false;
        try
        {
            metadataHandle.DangerousAddRef(ref firstHeld);
            payloadHandle.DangerousAddRef(ref secondHeld);
            using var message = new NativeMessage();
            message.SetDescriptors(metadataHandle.DangerousGetHandle().ToInt32(),
                payloadHandle.DangerousGetHandle().ToInt32());
            while (true)
            {
                CheckPeer(socket, peer, deadline.Token);
                nint sent = Send(socket.SafeHandle, ref message.Header, DontWait | NoSignal);
                if (sent == 1) { CheckPeer(socket, peer, deadline.Token); return; }
                if (sent != -1 || !Retryable()) throw Refused();
                await Task.Delay(10, deadline.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            try { if (secondHeld) payloadHandle.DangerousRelease(); }
            finally { if (firstHeld) metadataHandle.DangerousRelease(); }
        }
    }

    internal static async Task<Sources> ReceiveAsync(Socket socket, LinuxWorkerPeerBinding peer,
        CancellationToken token = default)
    {
        RequirePlatform();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var message = new NativeMessage();
        nint count;
        while (true)
        {
            CheckPeer(socket, peer, deadline.Token);
            message.Header.ControlLength = ControlBytes;
            message.Header.Flags = 0;
            count = Receive(socket.SafeHandle, ref message.Header, DontWait | CloseOnExec);
            if (count >= 0) break;
            if (!Retryable()) throw Refused();
            await Task.Delay(10, deadline.Token).ConfigureAwait(false);
        }
        byte[] ancillary = message.ControlData();
        var decoded = DecodeControl(ancillary, message.Header.Flags);
        var handles = decoded.Descriptors.Where(fd => fd >= 0).Distinct().Select(fd => new SafeFileHandle((IntPtr)fd, true)).ToArray();
        FileStream? metadata = null, payload = null;
        bool transferred = false;
        try
        {
            // Adopt every delivered descriptor before inspecting or rejecting the
            // packet. Extra/truncated/unexpected ancillary data cannot leak handles.
            CheckPeer(socket, peer, deadline.Token);
            if (count != 1 || Marshal.ReadByte(message.Data) != MessageMarker || !decoded.Exact || handles.Length != 2)
                throw Refused();
            var first = CheckFile(handles[0], DiskImageRawProfileVerificationService.MaximumMetadataBytes);
            var second = CheckFile(handles[1], DiskImageRawProfileVerificationService.MaximumCapacity);
            if (SameFile(first, second)) throw Refused();
            metadata = new(handles[0], FileAccess.Read, 128 * 1024, isAsync: false);
            payload = new(handles[1], FileAccess.Read, 128 * 1024, isAsync: false);
            metadata.Position = 0; payload.Position = 0;
            CheckPeer(socket, peer, deadline.Token);
            var result = new Sources(metadata, payload);
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred)
            {
                try { metadata?.Dispose(); }
                finally
                {
                    try { payload?.Dispose(); }
                    finally { foreach (var handle in handles) handle.Dispose(); }
                }
            }
        }
    }

    // Pure bounded decoder. Integers here are kernel-delivered descriptor slots,
    // never wire-provided handle numbers. It neither adopts nor closes anything.
    internal static (int[] Descriptors, bool Exact) DecodeControl(ReadOnlySpan<byte> bytes, int flags)
    {
        if (bytes.Length > ControlBytes) return ([], false);
        var descriptors = new List<int>();
        bool exact = (flags & ~CloseOnExec) == 0;
        int offset = 0, records = 0;
        while (bytes.Length - offset >= HeaderBytes)
        {
            var record = bytes[offset..];
            ulong declared = BinaryPrimitives.ReadUInt64LittleEndian(record);
            int level = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
            int type = BinaryPrimitives.ReadInt32LittleEndian(record[12..]);
            if (declared < HeaderBytes) { exact = false; break; }
            int available = (int)Math.Min(declared, (ulong)record.Length);
            if (level == 1 && type == 1) // SOL_SOCKET / SCM_RIGHTS
                exact &= ReadRights(record[..available], descriptors);
            else exact = false;
            records++;
            if (declared > (ulong)record.Length) { exact = false; offset = bytes.Length; break; }
            offset += (int)((declared + 7) & ~7UL);
        }
        exact &= offset == bytes.Length && records == 1 && descriptors.Count == 2 &&
            descriptors.All(fd => fd >= 0) && descriptors.Distinct().Count() == 2;
        return (descriptors.ToArray(), exact);
    }

    private static bool ReadRights(ReadOnlySpan<byte> record, List<int> descriptors)
    {
        for (int at = HeaderBytes; at + 4 <= record.Length; at += 4)
            descriptors.Add(BinaryPrimitives.ReadInt32LittleEndian(record[at..]));
        return (record.Length - HeaderBytes) % 4 == 0;
    }

    private static void CheckPeer(Socket socket, LinuxWorkerPeerBinding peer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!peer.OwnsConnection(socket) || !peer.Recheck()) throw Refused();
    }
    private static bool SameFile(FileStatus a, FileStatus b) =>
        a.Inode == b.Inode && a.DeviceMajor == b.DeviceMajor && a.DeviceMinor == b.DeviceMinor;
    private static FileStatus CheckFile(SafeFileHandle file, ulong maximum)
    {
        int flags = FileFlags(file, 3), descriptorFlags = FileFlags(file, 1); // F_GETFL / F_GETFD
        if (flags < 0 || (flags & 3) != 0 || (flags & 0x200000) != 0 || descriptorFlags < 0 || (descriptorFlags & 1) == 0 ||
            Statx(file, "", 0x1000 | 0x100, 0x7ff, out var status) != 0 || (status.Mask & 0x301) != 0x301 ||
            (status.Mode & 0xf000) != 0x8000 || status.Size is 0 || status.Size > maximum) throw Refused();
        return status;
    }
    private static void RequirePlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("recovery-transfer.linux-x64-required");
    }
    private static bool Retryable() => Marshal.GetLastPInvokeError() is 4 or 11;
    private static IOException Refused() => new("recovery-transfer.refused");

    private sealed class NativeMessage : IDisposable
    {
        internal MessageHeader Header;
        internal IntPtr Data;
        private IntPtr vector, control;
        internal NativeMessage()
        {
            try
            {
                Data = Marshal.AllocHGlobal(1); Marshal.WriteByte(Data, MessageMarker);
                vector = Marshal.AllocHGlobal(Marshal.SizeOf<IoVector>());
                control = Marshal.AllocHGlobal(ControlBytes); Marshal.Copy(new byte[ControlBytes], 0, control, ControlBytes);
                Marshal.StructureToPtr(new IoVector { Base = Data, Length = 1 }, vector, false);
                Header = new() { Vector = vector, VectorLength = 1, Control = control, ControlLength = ControlBytes };
            }
            catch { Dispose(); throw; }
        }
        internal void SetDescriptors(int first, int second)
        {
            Marshal.WriteInt64(control, 24); Marshal.WriteInt32(control, 8, 1); Marshal.WriteInt32(control, 12, 1);
            Marshal.WriteInt32(control, 16, first); Marshal.WriteInt32(control, 20, second); Header.ControlLength = 24;
        }
        internal byte[] ControlData()
        {
            if (Header.ControlLength > ControlBytes) throw Refused();
            byte[] bytes = new byte[(int)Header.ControlLength]; Marshal.Copy(control, bytes, 0, bytes.Length); return bytes;
        }
        public void Dispose()
        {
            Marshal.FreeHGlobal(control); Marshal.FreeHGlobal(vector); Marshal.FreeHGlobal(Data);
            control = vector = Data = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector { internal IntPtr Base; internal nuint Length; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MessageHeader
    {
        internal IntPtr Name; internal uint NameLength; internal IntPtr Vector; internal nuint VectorLength;
        internal IntPtr Control; internal nuint ControlLength; internal int Flags;
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileStatus
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }
    [DllImport("libc", EntryPoint = "sendmsg", SetLastError = true)]
    private static extern nint Send(SafeSocketHandle socket, ref MessageHeader message, int flags);
    [DllImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
    private static extern nint Receive(SafeSocketHandle socket, ref MessageHeader message, int flags);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FileFlags(SafeFileHandle file, int operation);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(SafeFileHandle file, string path, int flags, uint mask, out FileStatus status);
}
