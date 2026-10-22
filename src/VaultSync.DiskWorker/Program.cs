using System.Text.Json;
using VaultSync.Core.Services;

// Development-only transport host; deliberately absent from desktop launchers and
// release packaging. No shell, elevation, caller-selected paths or device opens.
bool peerMode = args.Length == 2 && args[0] == LinuxInventoryWorkerPeerChannel.Argument &&
    LinuxInventoryWorkerPeerChannel.Supported;
bool inspectMode = args.Length == 2 && args[0] == DiskImageInspectionWorkerService.Argument &&
    LinuxInventoryWorkerPeerChannel.Supported;
if (args.Length != 0 && !peerMode && !inspectMode)
    return 64;

TimeSpan limit = TimeSpan.FromSeconds(inspectMode ? 120 : 30);
using var deadline = new CancellationTokenSource(limit);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; deadline.Cancel(); };
try
{
    return await RunOperationAsync(inspectMode, peerMode || inspectMode ? args[1] : null, limit, deadline.Token);
}
catch (OperationCanceledException) { return 124; }
catch (TimeoutException) { return 124; }
catch (InvalidDataException) { return 65; }
catch (IOException) { return 74; }
catch (System.Net.Sockets.SocketException) { return 74; }
catch (UnauthorizedAccessException) { return 77; }
catch (JsonException) { return 65; }
catch (Exception) { return 70; } // Never leak metadata paths or identifiers through diagnostics.
finally { deadline.Cancel(); }


static async Task<int> RunOperationAsync(bool inspectMode, string? endpoint, TimeSpan limit, CancellationToken token)
{
    using LinuxInventoryWorkerPeerChannel? peer = endpoint is not null
        ? await LinuxInventoryWorkerPeerChannel.ConnectParentAsync(endpoint!, token) : null;
    using Stream input = Console.OpenStandardInput();
    using Stream output = Console.OpenStandardOutput();
    bool completed = inspectMode
        ? await DiskImageInspectionWorkerService.RunAsync(input, output, peer!, token).WaitAsync(limit)
        : await DiskInventoryWorkerService.RunAsync(input, output, token).WaitAsync(limit);
    if (completed && peer is not null)
        await peer.CompleteWorkerAsync(token);
    return completed ? 0 : 64;
}
