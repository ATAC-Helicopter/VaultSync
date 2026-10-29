using VaultSync.Core.Services;

if (args.Length == 1 && args[0] == "--installation-identity")
{
    try { await Console.OpenStandardOutput().WriteAsync(LinuxRecoveryMediaInstallation.BuildIdentity(LinuxRecoveryMediaInstallation.Role.Controller)); return 0; }
    catch (Exception) { return 64; }
}
if (args.Length != 0) return 64;
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromHours(24));
    var result = await LinuxRecoveryControllerClient.RunInstalledAsync(timeout.Token);
    return result switch
    {
        LinuxRecoveryControllerClient.Result.Restored => 0,
        LinuxRecoveryControllerClient.Result.AccessDenied => 77,
        LinuxRecoveryControllerClient.Result.Cancelled => 124,
        _ => 74
    };
}
catch (UnauthorizedAccessException) { return 77; }
catch (Exception) { return 74; }
