using VaultSync.Core.Services;
using VaultSync.RecoveryController;
using Avalonia;
using Avalonia.Media;
using System.Runtime.InteropServices;
using Startup = VaultSync.Core.Services.LinuxRecoveryStartupDiagnostics;

if (args.Length == 1 && args[0] == "--installation-identity")
{
    try { await Console.OpenStandardOutput().WriteAsync(LinuxRecoveryMediaInstallation.BuildIdentity(LinuxRecoveryMediaInstallation.Role.Controller)); return 0; }
    catch (Exception) { return 64; }
}
if (args.Length != 0) return 64;
Startup.Write(Startup.Role.Controller, Startup.Phase.ControllerEntry);
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromHours(24));
    using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; timeout.Cancel(); });
    using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; timeout.Cancel(); });
    using var installation = await LinuxRecoveryMediaInstallation.OpenAsync(LinuxRecoveryMediaInstallation.Role.Controller, timeout.Token);
    Startup.Write(Startup.Role.Controller, Startup.Phase.ControllerAdmitted);
    LinuxRecoveryControllerClient.Result result;
    if (installation.Description.ControllerMode == "headless")
        result = await LinuxRecoveryControllerClient.RunInstalledAsync(installation, timeout.Token);
    else
    {
        await installation.RequireDisplayAsync(timeout.Token);
        Startup.Write(Startup.Role.Controller, Startup.Phase.DisplayAdmitted);
        result = LinuxRecoveryControllerClient.Result.Failed;
        RecoveryApp.LifetimeToken = timeout.Token;
        RecoveryApp.Runner = async (presentation, token) =>
        {
            Startup.Write(Startup.Role.Controller, Startup.Phase.WindowOpened);
            result = await RecoveryControllerRunner.RunAsync(installation, presentation, token);
        };
        Startup.Write(Startup.Role.Controller, Startup.Phase.PlatformActivation);
        AppBuilder.Configure<RecoveryApp>().UsePlatformDetect()
            .With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software }, WmClass = "vaultsync-recovery" })
            .With(new FontManagerOptions { DefaultFamilyName = "avares://VaultSync.RecoveryController/Assets/Fonts/#Noto Sans" })
            .StartWithClassicDesktopLifetime(args);
    }
    Startup.Write(Startup.Role.Controller, Startup.Phase.ControllerResult);
    return result switch
    {
        LinuxRecoveryControllerClient.Result.Restored => 0,
        LinuxRecoveryControllerClient.Result.AccessDenied => 77,
        LinuxRecoveryControllerClient.Result.Cancelled => 124,
        _ => 74
    };
}
catch (UnauthorizedAccessException error) { Startup.Write(Startup.Role.Controller, Startup.Phase.ControllerFailed, Startup.Classify(error)); return 77; }
catch (Exception error) { Startup.Write(Startup.Role.Controller, Startup.Phase.ControllerFailed, Startup.Classify(error)); return 74; }
