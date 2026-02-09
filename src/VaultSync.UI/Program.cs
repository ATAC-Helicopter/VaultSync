using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Logging;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI;

internal static class Program
{
    private static Mutex? _instanceMutex;
    private static CancellationTokenSource? _activationListenerCts;
    private const string InstancePipeName = "VaultSync.UI.SingleInstancePipe";
    private static readonly string? PsPath = ResolvePsPath();

    [System.STAThread]
    public static void Main(string[] args)
    {
        DiagnosticsLogger.Initialize();
        DiagnosticsLogger.Record($"Process start. PID={Environment.ProcessId}, Args='{string.Join(' ', args)}'.");
        LogParentProcessInfo("startup");
        RegisterPosixSignals();
        CrashHandler.RegisterEarly();
        if (PatchInstallService.TryParsePatchArgs(args, out var request))
        {
            DiagnosticsLogger.Record("Patch installer mode detected.");
            UpdaterApp.SetPendingRequest(request);
            BuildUpdaterApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        _instanceMutex = new Mutex(true, "VaultSync.UI.SingleInstance", out var isFirstInstance);
        DiagnosticsLogger.Record($"Instance mutex acquired. IsFirst={isFirstInstance}.");
        if (!isFirstInstance)
        {
            DiagnosticsLogger.Record("Second instance detected. Signaling existing instance.");
            _instanceMutex.Dispose();
            _instanceMutex = null;
            TrySignalExistingInstance();
            return;
        }

        try
        {
            _activationListenerCts = new CancellationTokenSource();
            _ = Task.Run(() => ListenForActivationRequests(_activationListenerCts.Token));
            try
            {
                DiagnosticsLogger.Record("Starting Avalonia app (native render).");
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (InvalidOperationException ex) when (
                OperatingSystem.IsMacOS() &&
                ex.Message.Contains("RenderTimer", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[Startup] Native render timer failed: {ex.Message}. Falling back to software rendering.");
                DiagnosticsLogger.Record($"Native render timer failed. Falling back to software. Error={ex.Message}");
                BuildAvaloniaApp(useSoftwareFallback: true).StartWithClassicDesktopLifetime(args);
            }
        }
        finally
        {
            if (_activationListenerCts is not null)
            {
                _activationListenerCts.Cancel();
                _activationListenerCts.Dispose();
                _activationListenerCts = null;
            }
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            DiagnosticsLogger.Record("Process exit cleanup complete.");
        }
    }

    private static void TrySignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                InstancePipeName,
                PipeDirection.Out);
            try
            {
                client.Connect(500);
                client.WriteByte(1);
                DiagnosticsLogger.Record("Signaled existing instance.");
            }
            catch (TimeoutException)
            {
                // Ignore timeout: treat as no active instance.
                DiagnosticsLogger.Record("Signal existing instance timed out.");
            }
        }
        catch
        {
            // Best-effort: if we can't reach the existing instance, just exit.
            DiagnosticsLogger.Record("Failed to signal existing instance.");
        }
    }

    private static async Task ListenForActivationRequests(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    InstancePipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token);
                _ = server.ReadByte();
                DiagnosticsLogger.Record("Received activation signal.");
                App.ActivateMainWindowFromSignal();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore and keep listening.
            }
        }
    }

    private static void RegisterPosixSignals()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                var info = GetParentProcessInfo();
                DiagnosticsLogger.Record($"POSIX signal: SIGTERM (cancel={ctx.Cancel}). Parent={info}");
                if (string.Equals(Environment.GetEnvironmentVariable("VAULTSYNC_IGNORE_SIGTERM"), "1", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Cancel = true;
                    DiagnosticsLogger.Record("SIGTERM ignored due to VAULTSYNC_IGNORE_SIGTERM=1.");
                }
            });
            PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
            {
                DiagnosticsLogger.Record($"POSIX signal: SIGINT (cancel={ctx.Cancel}).");
            });
            PosixSignalRegistration.Create(PosixSignal.SIGQUIT, ctx =>
            {
                DiagnosticsLogger.Record($"POSIX signal: SIGQUIT (cancel={ctx.Cancel}).");
            });
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
            {
                DiagnosticsLogger.Record($"POSIX signal: SIGHUP (cancel={ctx.Cancel}).");
            });
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Record($"POSIX signal registration failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private static void LogParentProcessInfo(string stage)
    {
        var info = GetParentProcessInfo();
        DiagnosticsLogger.Record($"Parent process ({stage}): {info}");
    }

    private static string GetParentProcessInfo()
    {
        if (OperatingSystem.IsWindows())
            return $"pid={Environment.ProcessId}, ppid=unsupported";

        try
        {
            var pid = Environment.ProcessId;
            var ppid = RunPs($"-o ppid= -p {pid}").Trim();
            if (string.IsNullOrWhiteSpace(ppid))
                return "ppid=unknown";

            var comm = RunPs($"-p {ppid} -o comm=").Trim();
            if (string.IsNullOrWhiteSpace(comm))
                return $"ppid={ppid}";

            return $"ppid={ppid}, comm={comm}";
        }
        catch (Exception ex)
        {
            return $"ppid=error:{ex.GetType().Name}";
        }
    }

    private static string RunPs(string arguments)
    {
        if (string.IsNullOrWhiteSpace(PsPath))
            return string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName = PsPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc is null)
            return string.Empty;
        proc.WaitForExit(2000);
        return proc.StandardOutput.ReadToEnd();
    }

    private static string? ResolvePsPath()
    {
        if (OperatingSystem.IsWindows())
            return null;

        const string binPs = "/bin/ps";
        if (File.Exists(binPs))
            return binPs;

        const string usrBinPs = "/usr/bin/ps";
        if (File.Exists(usrBinPs))
            return usrBinPs;

        return null;
    }


    public static AppBuilder BuildAvaloniaApp(bool useSoftwareFallback = false)
    {
        var builder = AppBuilder.Configure<App>();
        if (useSoftwareFallback && OperatingSystem.IsMacOS())
        {
            builder = builder.UsePlatformDetect().With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = new[] { AvaloniaNativeRenderingMode.Software }
            });
        }
        else
        {
            builder = builder.UsePlatformDetect();
        }

        return builder
            // Avoid spamming stdout/in-app logs with Avalonia internals (e.g., binding trace).
            .LogToTrace(LogEventLevel.Warning);
    }

    private static AppBuilder BuildUpdaterApp()
        => AppBuilder.Configure<UpdaterApp>()
            .UsePlatformDetect()
            .LogToTrace(LogEventLevel.Warning);
}
