using System.Runtime.InteropServices;
using VaultSync.Core.Services;

if (args.Length == 1 && args[0] == "--installation-identity")
{
    try { await Console.OpenStandardOutput().WriteAsync(LinuxRecoveryMediaInstallation.BuildIdentity(LinuxRecoveryMediaInstallation.Role.Host)); return 0; }
    catch (Exception) { return 64; }
}
if (args.Length != 0) return 64;
if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return 64;
using var shutdown = new CancellationTokenSource();
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; shutdown.Cancel(); });
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; shutdown.Cancel(); });
try { return await LinuxRecoveryHost.RunAsync(shutdown.Token); }
catch (OperationCanceledException) { return 124; }
catch (UnauthorizedAccessException) { return 77; }
catch (Exception) { return 74; } // Numeric role outcome only; never disclose image paths or technical payloads.
