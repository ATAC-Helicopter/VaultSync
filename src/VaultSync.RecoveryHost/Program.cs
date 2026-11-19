using System.Runtime.InteropServices;
using VaultSync.Core.Services;
using Startup = VaultSync.Core.Services.LinuxRecoveryStartupDiagnostics;

if (args.Length == 1 && args[0] == "--installation-identity")
{
    try { await Console.OpenStandardOutput().WriteAsync(LinuxRecoveryMediaInstallation.BuildIdentity(LinuxRecoveryMediaInstallation.Role.Host)); return 0; }
    catch (Exception) { return 64; }
}
bool capture = args.Length == 1 && args[0] == "--capture";
if (args.Length != 0 && !capture) return 64;
if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return 64;
using var shutdown = new CancellationTokenSource();
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; shutdown.Cancel(); });
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; shutdown.Cancel(); });
try { return capture ? await LinuxRecoveryCaptureHost.RunAsync(shutdown.Token) : await LinuxRecoveryHost.RunAsync(shutdown.Token); }
catch (OperationCanceledException error) { Startup.Write(Startup.Role.Host, Startup.Phase.HostFailed, Startup.Classify(error)); return 124; }
catch (UnauthorizedAccessException error) { Startup.Write(Startup.Role.Host, Startup.Phase.HostFailed, Startup.Classify(error)); return 77; }
catch (Exception error) { Startup.Write(Startup.Role.Host, Startup.Phase.HostFailed, Startup.Classify(error)); return 74; }
