using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI;

public sealed partial class SettingsViewModel
{
    private void InspectRepositoryWriter(BackupDestinationViewModel? destination)
    {
        _ = DetachedTask.RunAsync(
            () => InspectRepositoryWriterAsync(destination),
            nameof(InspectRepositoryWriterAsync));
    }

    internal async Task InspectRepositoryWriterAsync(BackupDestinationViewModel? destination)
    {
        if (destination is null || string.IsNullOrWhiteSpace(destination.Path))
            return;

        destination.IsRepositoryWriterBusy = true;
        destination.IsWriterTakeoverConfirmationVisible = false;
        try
        {
            AppConfig config = await Task.Run(_configStore.Load);
            NetworkCredentialProfile? profile = ResolveCredential(config, destination.CredentialName);
            BackupDestination model = BuildDestinationModel(destination);
            DestinationResolution resolution = await Task.Run(() => _networkMountService.PrepareDestination(model, profile));
            if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.EffectivePath))
            {
                ApplyRepositoryWriterInspection(
                    destination,
                    new RepositoryLeaseInspection(
                        RepositoryLeaseState.Unavailable,
                        null,
                        resolution.Message));
                return;
            }

            try
            {
                RepositoryLeaseInspection inspection = await Task.Run(
                    () => _repositoryLeaseService.Inspect(resolution.EffectivePath));
                ApplyRepositoryWriterInspection(destination, inspection, resolution.EffectivePath);
            }
            finally
            {
                NetworkMountService.Cleanup(resolution);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ApplyRepositoryWriterInspection(
                destination,
                new RepositoryLeaseInspection(RepositoryLeaseState.Unavailable, null, ex.Message));
        }
        finally
        {
            destination.IsRepositoryWriterBusy = false;
        }
    }

    private static void ReviewStaleWriter(BackupDestinationViewModel? destination)
    {
        if (destination?.CanReviewStaleWriter == true)
            destination.IsWriterTakeoverConfirmationVisible = true;
    }

    private static void CancelStaleWriterTakeover(BackupDestinationViewModel? destination)
    {
        if (destination is not null)
            destination.IsWriterTakeoverConfirmationVisible = false;
    }

    private void ConfirmStaleWriterTakeover(BackupDestinationViewModel? destination)
    {
        _ = DetachedTask.RunAsync(
            () => ConfirmStaleWriterTakeoverAsync(destination),
            nameof(ConfirmStaleWriterTakeoverAsync));
    }

    internal async Task ConfirmStaleWriterTakeoverAsync(BackupDestinationViewModel? destination)
    {
        if (destination is null ||
            !destination.CanReviewStaleWriter ||
            string.IsNullOrWhiteSpace(destination.StaleWriterNonce) ||
            string.IsNullOrWhiteSpace(destination.InspectedRepositoryRoot))
        {
            return;
        }

        string expectedNonce = destination.StaleWriterNonce;
        string expectedRoot = destination.InspectedRepositoryRoot;
        destination.IsRepositoryWriterBusy = true;
        destination.IsWriterTakeoverConfirmationVisible = false;

        try
        {
            AppConfig config = await Task.Run(_configStore.Load);
            NetworkCredentialProfile? profile = ResolveCredential(config, destination.CredentialName);
            BackupDestination model = BuildDestinationModel(destination);
            DestinationResolution resolution = await Task.Run(() => _networkMountService.PrepareDestination(model, profile));
            if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.EffectivePath))
            {
                ApplyRepositoryWriterInspection(
                    destination,
                    new RepositoryLeaseInspection(RepositoryLeaseState.Unavailable, null, resolution.Message));
                return;
            }

            try
            {
                if (!PathsEqual(expectedRoot, resolution.EffectivePath))
                {
                    ApplyRepositoryWriterInspection(
                        destination,
                        new RepositoryLeaseInspection(
                            RepositoryLeaseState.Invalid,
                            null,
                            LS(
                                "Settings.Destinations.Writer.RepositoryChanged",
                                "The destination now resolves to a different repository. Check its writer again.")));
                    return;
                }

                RepositoryLeaseInspection current = await Task.Run(
                    () => _repositoryLeaseService.Inspect(resolution.EffectivePath));
                if (current.State != RepositoryLeaseState.Stale ||
                    current.Lease is null ||
                    !string.Equals(current.Lease.Nonce, expectedNonce, StringComparison.Ordinal))
                {
                    ApplyRepositoryWriterInspection(destination, current, resolution.EffectivePath);
                    return;
                }

                string installationId = await Task.Run(_installationIdentityProvider.GetOrCreate);
                RepositoryLeaseAcquireResult takeover = await Task.Run(() =>
                    _repositoryLeaseService.TakeOverStale(
                        resolution.EffectivePath,
                        expectedNonce,
                        new RepositoryLeaseRequest(
                            installationId,
                            Environment.MachineName,
                            "stale-takeover-confirmation",
                            _appVersion)));

                if (!takeover.Acquired)
                {
                    ApplyRepositoryWriterInspection(destination, takeover.Inspection, resolution.EffectivePath);
                    return;
                }

                using (takeover.Handle)
                {
                    // Acquiring with the inspected nonce atomically records the old
                    // writer as evidence. Releasing immediately lets the user's next
                    // real operation acquire its own correctly named lease.
                }

                RepositoryLeaseInspection available = await Task.Run(
                    () => _repositoryLeaseService.Inspect(resolution.EffectivePath));
                ApplyRepositoryWriterInspection(destination, available, resolution.EffectivePath);
                destination.RepositoryWriterDetails =
                    LS(
                        "Settings.Destinations.Writer.ClearedDetail",
                        "The stale writer was preserved as evidence and cleared. The next operation can write safely.");
                SaveStatus = string.Format(
                    CultureInfo.CurrentCulture,
                    LS("Settings.Destinations.Writer.Cleared", "Cleared the stale repository writer for '{0}'."),
                    destination.DisplayName);
            }
            finally
            {
                NetworkMountService.Cleanup(resolution);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ApplyRepositoryWriterInspection(
                destination,
                new RepositoryLeaseInspection(RepositoryLeaseState.Unavailable, null, ex.Message));
        }
        finally
        {
            destination.IsRepositoryWriterBusy = false;
        }
    }

    internal static void ApplyRepositoryWriterInspection(
        BackupDestinationViewModel destination,
        RepositoryLeaseInspection inspection,
        string? repositoryRoot = null)
    {
        destination.IsWriterTakeoverConfirmationVisible = false;
        destination.CanReviewStaleWriter = inspection.State == RepositoryLeaseState.Stale && inspection.Lease is not null;
        destination.StaleWriterNonce = destination.CanReviewStaleWriter
            ? inspection.Lease!.Nonce
            : string.Empty;
        destination.InspectedRepositoryRoot = destination.CanReviewStaleWriter
            ? repositoryRoot ?? string.Empty
            : string.Empty;

        destination.RepositoryWriterStatus = inspection.State switch
        {
            RepositoryLeaseState.Available => LS("Settings.Destinations.Writer.Status.Available", "Available"),
            RepositoryLeaseState.Active => LS("Settings.Destinations.Writer.Status.Active", "In use"),
            RepositoryLeaseState.Stale => LS("Settings.Destinations.Writer.Status.Stale", "Needs review"),
            RepositoryLeaseState.Invalid => LS("Settings.Destinations.Writer.Status.Invalid", "Invalid state"),
            _ => LS("Settings.Destinations.Writer.Status.Unavailable", "Unavailable")
        };

        if (inspection.Lease is null)
        {
            destination.RepositoryWriterDetails = inspection.State == RepositoryLeaseState.Available
                ? LS(
                    "Settings.Destinations.Writer.AvailableDetail",
                    "No VaultSync writer currently holds this repository.")
                : inspection.Message;
            return;
        }

        RepositoryLeaseSnapshot lease = inspection.Lease;
        string owner = string.IsNullOrWhiteSpace(lease.HostLabel)
            ? LS("Settings.Destinations.Writer.UnknownHost", "Unknown host")
            : lease.HostLabel;
        string identity = lease.InstallationId.Length > 8 ? lease.InstallationId[..8] : lease.InstallationId;
        destination.RepositoryWriterDetails = string.Format(
            CultureInfo.CurrentCulture,
            LS(
                "Settings.Destinations.Writer.LeaseDetail",
                "{0} · identity {1} · {2} · app {3} · heartbeat {4:u} · expires {5:u}"),
            owner,
            identity,
            lease.Operation,
            lease.AppVersion,
            lease.HeartbeatUtc,
            lease.ExpiresUtc);
    }

    private static string LS(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.OrdinalIgnoreCase)
            ? fallback
            : value;
    }

    private static NetworkCredentialProfile? ResolveCredential(AppConfig config, string? credentialName) =>
        string.IsNullOrWhiteSpace(credentialName)
            ? null
            : config.Network.Credentials.FirstOrDefault(candidate =>
                candidate.Name.Equals(credentialName, StringComparison.OrdinalIgnoreCase));

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
