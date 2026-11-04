using System.Globalization;

namespace VaultSync.Core.Services;

/// <summary>Local fixed startup codes only. Never an authority input or network diagnostic sender.</summary>
internal static class LinuxRecoveryStartupDiagnostics
{
    internal enum Role { Host = 1, Controller = 2 }
    internal enum Phase
    {
        HostAdmission = 1, HostAdmitted = 2, HostCreate = 3, HostCreated = 4,
        ChildStart = 5, ChildStarted = 6, Accept = 7, Peer = 8, Hello = 9,
        Storage = 10, Owner = 11, OwnerTerminal = 12, Receipt = 13,
        Cleanup = 14, Cleaned = 15, CreateFailed = 16, ConnectFailed = 17,
        CleanupFailed = 18, HostFailed = 19, ChildStatusRejected = 20,
        RuntimeOwned = 21, ControllerGroup = 22, ListenerBind = 23, SocketIdentity = 24,
        ControllerStopped = 25, SocketRemoval = 26, GroupRemoval = 27,
        ControllerEntry = 101, ControllerAdmitted = 102, DisplayAdmitted = 103,
        PlatformActivation = 104, WindowOpened = 105, ClientEntry = 106,
        ControllerConnect = 107, ControllerPeer = 108, ControllerHello = 109,
        ControllerResult = 110, ControllerFailed = 111,
        NativePipe = 120, NativeGroup = 121, NativeSession = 122, NativeIds = 123,
        NativeCapabilities = 124, NativeDescriptors = 125, NativeExec = 126
    }
    internal enum Error { None, InvalidData, Denied, Cancelled, Io, MissingMethod, NativeLibrary, Unsupported, Timeout, Unexpected }
    internal const int MaximumBytes = 4096, MaximumRecords = 32, MaximumRecordBytes = 32;

    internal static Error Classify(Exception error)
    {
        for (int depth = 0; depth < 3 && error is TypeInitializationException or System.Reflection.TargetInvocationException; depth++)
        {
            if (error.InnerException is null) break;
            error = error.InnerException;
        }
        return error switch
        {
            InvalidDataException => Error.InvalidData, UnauthorizedAccessException => Error.Denied,
            OperationCanceledException => Error.Cancelled, TimeoutException => Error.Timeout,
            MissingMethodException => Error.MissingMethod, DllNotFoundException or BadImageFormatException => Error.NativeLibrary,
            PlatformNotSupportedException => Error.Unsupported, IOException => Error.Io, _ => Error.Unexpected
        };
    }

    internal static void Write(Role role, Phase phase, Error error = Error.None)
    {
        if (!Valid(role, phase, error)) return;
        try
        {
            Console.Error.Write(string.Create(CultureInfo.InvariantCulture, $"VSREC1|{(int)role}|{(int)phase}|{(int)error}\n"));
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private static bool Valid(Role role, Phase phase, Error error) => Enum.IsDefined(role) && Enum.IsDefined(phase) &&
        Enum.IsDefined(error) && (role == Role.Controller ? (int)phase >= 100 : (int)phase < 100);

    internal static async Task ReadControllerAsync(Stream input, Action<Phase, Error> forward, CancellationToken token)
    {
        byte[] buffer = new byte[256], line = new byte[MaximumRecordBytes];
        int total = 0, length = 0, records = 0;
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                if (total > MaximumBytes - read) throw Invalid();
                total += read;
                for (int i = 0; i < read; i++)
                {
                    byte value = buffer[i];
                    if (value == '\n')
                    {
                        if (++records > MaximumRecords || !Parse(line.AsSpan(0, length), out var phase, out var error)) throw Invalid();
                        forward(phase, error); length = 0;
                    }
                    else
                    {
                        if (length == line.Length || value is < 32 or > 126) throw Invalid();
                        line[length++] = value;
                    }
                }
            }
            if (length != 0) throw Invalid();
        }
        finally { input.Dispose(); }
    }

    private static bool Parse(ReadOnlySpan<byte> line, out Phase phase, out Error error)
    {
        phase = default; error = default;
        string[] fields = System.Text.Encoding.ASCII.GetString(line).Split('|');
        if (fields.Length != 4 || fields[0] != "VSREC1" || fields[1] != "2" ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
            !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out int failure) ||
            fields[2] != value.ToString(CultureInfo.InvariantCulture) ||
            fields[3] != failure.ToString(CultureInfo.InvariantCulture)) return false;
        phase = (Phase)value; error = (Error)failure;
        return Valid(Role.Controller, phase, error);
    }
    private static InvalidDataException Invalid() => new("recovery-controller.invalid-startup-status");
}
