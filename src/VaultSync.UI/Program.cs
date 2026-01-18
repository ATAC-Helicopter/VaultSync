using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI;

internal static class Program
{
    private static Mutex? _instanceMutex;
    private static CancellationTokenSource? _activationListenerCts;
    private const string InstancePipeName = "VaultSync.UI.SingleInstancePipe";

    [System.STAThread]
    public static void Main(string[] args)
    {
        CrashHandler.RegisterEarly();
        if (PatchInstallService.TryParsePatchArgs(args, out var request))
        {
            UpdaterApp.SetPendingRequest(request);
            BuildUpdaterApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        _instanceMutex = new Mutex(true, "VaultSync.UI.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            TrySignalExistingInstance();
            return;
        }

        try
        {
            _activationListenerCts = new CancellationTokenSource();
            _ = Task.Run(() => ListenForActivationRequests(_activationListenerCts.Token));
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
            }
            catch (TimeoutException)
            {
                // Ignore timeout: treat as no active instance.
            }
        }
        catch
        {
            // Best-effort: if we can't reach the existing instance, just exit.
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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static AppBuilder BuildUpdaterApp()
        => AppBuilder.Configure<UpdaterApp>()
            .UsePlatformDetect()
            .LogToTrace();
}
