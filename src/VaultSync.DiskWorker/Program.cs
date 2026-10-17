using System.Text.Json;
using VaultSync.Core.Services;

// Development-only transport host; deliberately absent from desktop launchers and
// release packaging. No shell, elevation, caller-selected paths or device opens.
if (args.Length != 0)
    return 64;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; deadline.Cancel(); };
try
{
    using Stream input = Console.OpenStandardInput();
    using Stream output = Console.OpenStandardOutput();
    bool completed = await DiskInventoryWorkerService.RunAsync(input, output, deadline.Token)
        .WaitAsync(TimeSpan.FromSeconds(30));
    return completed ? 0 : 64;
}
catch (OperationCanceledException) { return 124; }
catch (TimeoutException) { return 124; }
catch (InvalidDataException) { return 65; }
catch (IOException) { return 74; }
catch (UnauthorizedAccessException) { return 77; }
catch (JsonException) { return 65; }
catch (Exception) { return 70; } // Never leak metadata paths or identifiers through diagnostics.
finally { deadline.Cancel(); }
