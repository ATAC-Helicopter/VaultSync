using System.Globalization;
using System.Text;

namespace VaultSync.Core.Services;

/// <summary>Fixed installed offline capture role. Never runs an unprivileged caller's command or path.</summary>
internal static class LinuxRecoveryCaptureHost
{
    internal const string OutputParent = LinuxRecoveryStorageLease.Store + "/captures";

    internal static async Task<int> RunAsync(CancellationToken token)
    {
        using var installation = await LinuxRecoveryMediaInstallation.OpenAsync(LinuxRecoveryMediaInstallation.Role.Host, token).ConfigureAwait(false);
        RequireMode(installation.Description.ControllerMode);
        var console = installation.ConsoleText();
        var build = BuildInformationService.Create(typeof(LinuxRecoveryCaptureHost).Assembly);
        string version = RequireBuild(build, installation.Description.Commit);
        LinuxRecoveryCaptureExportAssessment? exported = null;
        var outcome = await RunSessionAsync(async cancellation =>
        {
            var expected = await LinuxBlockInventoryService.ObserveAsync(cancellation).ConfigureAwait(false);
            Guid operation = Guid.NewGuid();
            string? selected = await LinuxRecoveryCaptureConsole.SelectAsync(operation, expected, console.Text,
                console.Culture, cancellation).ConfigureAwait(false);
            if (selected is null) return null;
            var fresh = await LinuxBlockInventoryService.ObserveAsync(cancellation).ConfigureAwait(false);
            var before = expected.Devices.Where(value => value.KernelName == selected).Take(2).ToArray();
            var after = fresh.Devices.Where(value => value.KernelName == selected).Take(2).ToArray();
            if (expected.BootId != fresh.BootId || expected.MountNamespace != fresh.MountNamespace ||
                before.Length != 1 || after.Length != 1 || before[0] != after[0]) throw LinuxRecoveryMediaInstallation.Invalid();
            installation.RecheckBoot(LinuxRecoveryMediaInstallation.Role.Host); installation.Recheck();
            var request = new LinuxRawImageCaptureRequest(Guid.NewGuid(), operation, Guid.NewGuid(),
                Guid.NewGuid(), 1, version, installation.Description.Commit);
            // Reserve the second payload before capture so ENOSPC cannot consume all
            // available space with an original that cannot be shared with the interface.
            var reservation = LinuxRecoveryCaptureExport.Reserve(request, before[0].Capacity, expected, fresh, selected);
            LinuxRawImageCaptureAssessment captured;
            try
            {
                captured = await LinuxRawImageCaptureService.CaptureAsync(request, fresh, selected, OutputParent,
                    cancellation).ConfigureAwait(false);
            }
            catch
            {
                await reservation.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            exported = await FinishExportAsync(captured, reservation, async () =>
            {
                using var original = reservation.OpenOriginal();
                return await LinuxRecoveryCaptureExport.CopyAsync(request, captured, original.Metadata, original.Payload,
                    reservation, original.Recheck, DiskImageInspectionWorkerClient.InspectAsync, cancellation).ConfigureAwait(false);
            }, cancellation).ConfigureAwait(false);
            return captured;
        }, (result, cancellation) => WriteResult(console.Text, console.Culture, result, exported, cancellation),
            lifetime => MonitorAsync(installation, lifetime), token).ConfigureAwait(false);
        return ExitCode(outcome, exported);
    }

    internal static int ExitCode(SessionOutcome outcome, LinuxRecoveryCaptureExportAssessment? exported) =>
        outcome.ExitCode == 0 && exported?.State != LinuxRecoveryCaptureExportState.PublishedVerified ? 74 : outcome.ExitCode;

    // Every failure after capture publication belongs to export/host health. Neither
    // reader disposal nor asynchronous copy cleanup may erase the captured result.
    internal static async Task<LinuxRecoveryCaptureExportAssessment?> FinishExportAsync(
        LinuxRawImageCaptureAssessment captured, LinuxRecoveryCaptureExport.ICopy reservation,
        Func<Task<LinuxRecoveryCaptureExportAssessment>> export, CancellationToken token)
    {
        LinuxRecoveryCaptureExportAssessment? result = null;
        try
        {
            if (captured.State == LinuxRawImageCaptureState.PublishedVerified)
                result = await export().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { result = new(reservation.Published ? LinuxRecoveryCaptureExportState.PublicationUnconfirmed : LinuxRecoveryCaptureExportState.Interrupted); }
        catch (Exception)
        { result = new(reservation.Published ? LinuxRecoveryCaptureExportState.PublicationUnconfirmed : LinuxRecoveryCaptureExportState.Failed); }
        finally
        {
            try { await reservation.DisposeAsync().ConfigureAwait(false); }
            catch (Exception)
            { result = new(reservation.Published ? LinuxRecoveryCaptureExportState.PublicationUnconfirmed : LinuxRecoveryCaptureExportState.Failed); }
        }
        return result;
    }

    internal static string RequireBuild(BuildInformation build, string commit)
    {
        if (build.SourceCommit != commit || build.Version == BuildInformation.Unknown || !DiskImageOriginRecordCodec.ValidTool(build.Version, commit))
            throw LinuxRecoveryMediaInstallation.Invalid();
        return build.Version;
    }

    internal sealed record SessionOutcome(LinuxRawImageCaptureAssessment? Capture, int ExitCode);

    // Durable capture state is independent of presentation and host health. In particular,
    // a failed display or boot monitor cannot turn a committed image into an interrupted one.
    internal static async Task<SessionOutcome> RunSessionAsync(
        Func<CancellationToken, Task<LinuxRawImageCaptureAssessment?>> capture,
        Action<LinuxRawImageCaptureAssessment, CancellationToken> display,
        Func<CancellationTokenSource, Task> monitorFactory, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var monitor = monitorFactory(lifetime);
        LinuxRawImageCaptureAssessment? result = null;
        Exception? failure = null;
        try
        {
            result = await capture(lifetime.Token).ConfigureAwait(false);
            if (result is not null)
            {
                // Allow a bounded result display even when cancellation raced the commit point.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                display(result, deadline.Token);
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await monitor.ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { failure ??= error; }
        }
        if (result is null && failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return new(result, failure is not null ? 74 : result is null ? 76 :
            result.State == LinuxRawImageCaptureState.PublishedVerified ? 0 : 74);
    }

    internal static void RequireMode(string mode)
    { if (mode != "capture") throw LinuxRecoveryMediaInstallation.Invalid(); }

    private static void WriteResult(Func<string, string?> text, CultureInfo culture,
        LinuxRawImageCaptureAssessment result, LinuxRecoveryCaptureExportAssessment? exported, CancellationToken token)
    {
        string statusKey = result.State switch
        {
            LinuxRawImageCaptureState.Invalid => "Status.Invalid",
            LinuxRawImageCaptureState.Unsupported => "Status.Unsupported",
            LinuxRawImageCaptureState.Refused => "Status.Refused",
            LinuxRawImageCaptureState.Incomplete => "Status.Incomplete",
            LinuxRawImageCaptureState.Interrupted => "Status.Interrupted",
            LinuxRawImageCaptureState.Failed => "Status.Failed",
            LinuxRawImageCaptureState.PublicationUnconfirmed => "Status.PublicationUnconfirmed",
            LinuxRawImageCaptureState.PublishedVerified => "Status.PublishedVerified",
            _ => throw LinuxRecoveryMediaInstallation.Invalid()
        };
        string message = LinuxRecoveryCaptureConsole.Format(text, culture, "Result", LinuxRecoveryCaptureConsole.Format(text, culture, statusKey),
            result.CopiedBytes.ToString("N0", culture)) + "\n";
        if (result.State == LinuxRawImageCaptureState.PublishedVerified)
            message += LinuxRecoveryCaptureConsole.Format(text, culture, "Published", result.ImageDirectoryName!, result.ManifestRoot!) + "\n";
        if (exported is not null)
        {
            string exportKey = exported.State switch
            {
                LinuxRecoveryCaptureExportState.Failed => "Export.Status.Failed",
                LinuxRecoveryCaptureExportState.Interrupted => "Export.Status.Interrupted",
                LinuxRecoveryCaptureExportState.PublicationUnconfirmed => "Export.Status.PublicationUnconfirmed",
                LinuxRecoveryCaptureExportState.PublishedVerified => "Export.Status.PublishedVerified",
                _ => throw LinuxRecoveryMediaInstallation.Invalid()
            };
            message += LinuxRecoveryCaptureConsole.Format(text, culture, "ExportResult",
                LinuxRecoveryCaptureConsole.Format(text, culture, exportKey)) + "\n";
            if (exported.State == LinuxRecoveryCaptureExportState.PublishedVerified)
                message += LinuxRecoveryCaptureConsole.Format(text, culture, "Export.Published",
                    LinuxRecoveryCaptureExport.Root + "/" + result.ImageDirectoryName) + "\n";
        }
        using var access = LinuxRecoveryLocalConsole.OpenPhysical();
        access.WaitForActive(token); access.ValidateContext(); access.Write(Encoding.UTF8.GetBytes(message), token);
    }

    private static async Task MonitorAsync(LinuxRecoveryMediaInstallation installation, CancellationTokenSource lifetime)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                installation.RecheckBoot(LinuxRecoveryMediaInstallation.Role.Host);
        }
        catch
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }
}
