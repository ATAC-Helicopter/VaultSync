using System;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RepositoryWriterUiTests
{
    [Fact]
    public void WriterInspection_ShowsShortOwnerEvidenceAndOnlyOffersStaleReview()
    {
        var destination = new BackupDestinationViewModel();
        var lease = new RepositoryLeaseSnapshot(
            RepositoryLeaseService.CurrentProtocolVersion,
            "1234567890abcdef1234567890abcdef",
            "Studio Mac",
            42,
            "metadata-export",
            "abcdefabcdefabcdefabcdefabcdefab",
            "1.8.7",
            new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 16, 10, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 16, 10, 6, 0, TimeSpan.Zero));

        SettingsViewModel.ApplyRepositoryWriterInspection(
            destination,
            new RepositoryLeaseInspection(RepositoryLeaseState.Active, lease, "busy"),
            "/shared/repository");

        Assert.Equal("In use", destination.RepositoryWriterStatus);
        Assert.Contains("Studio Mac", destination.RepositoryWriterDetails, StringComparison.Ordinal);
        Assert.Contains("identity 12345678", destination.RepositoryWriterDetails, StringComparison.Ordinal);
        Assert.Contains("metadata-export", destination.RepositoryWriterDetails, StringComparison.Ordinal);
        Assert.DoesNotContain(lease.InstallationId, destination.RepositoryWriterDetails, StringComparison.Ordinal);
        Assert.False(destination.CanReviewStaleWriter);

        SettingsViewModel.ApplyRepositoryWriterInspection(
            destination,
            new RepositoryLeaseInspection(RepositoryLeaseState.Stale, lease, "stale"),
            "/shared/repository");

        Assert.Equal("Needs review", destination.RepositoryWriterStatus);
        Assert.True(destination.CanReviewStaleWriter);
        Assert.Equal(lease.Nonce, destination.StaleWriterNonce);
        Assert.Equal("/shared/repository", destination.InspectedRepositoryRoot);

        SettingsViewModel.ApplyRepositoryWriterInspection(
            destination,
            new RepositoryLeaseInspection(RepositoryLeaseState.Available, null, "available"));

        Assert.Equal("Available", destination.RepositoryWriterStatus);
        Assert.False(destination.CanReviewStaleWriter);
        Assert.Empty(destination.StaleWriterNonce);
        Assert.Empty(destination.InspectedRepositoryRoot);
    }

    [Fact]
    public async Task ConfirmedStaleWriterTakeover_PreservesEvidenceAndReturnsRepositoryToAvailable()
    {
        using var configScope = new TestAppConfigScope();
        using var repository = new TempDirectory();
        AppConfigStore.Save(new AppConfig());

        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero));
        var leaseService = new RepositoryLeaseService(clock, TimeSpan.Zero);
        RepositoryLeaseAcquireResult acquired = leaseService.TryAcquire(
            repository.Path,
            new RepositoryLeaseRequest(
                Guid.NewGuid().ToString("N"),
                "Other machine",
                "metadata-export",
                "1.8.7",
                TimeSpan.FromMinutes(5)));
        using RepositoryLeaseHandle oldWriter = Assert.IsType<RepositoryLeaseHandle>(acquired.Handle);
        clock.Advance(TimeSpan.FromMinutes(6));

        var settings = new SettingsViewModel(
            new LocalizationService(),
            repositoryLeaseService: leaseService,
            installationIdentityProvider: new FixedIdentityProvider(),
            appVersion: "1.8.7");
        var destination = new BackupDestinationViewModel
        {
            Alias = "Shared",
            Path = repository.Path,
            PreMounted = true,
            EnableMetadataSync = true
        };

        await settings.InspectRepositoryWriterAsync(destination);

        Assert.True(destination.CanReviewStaleWriter);
        Assert.Equal(oldWriter.Lease.Nonce, destination.StaleWriterNonce);

        await settings.ConfirmStaleWriterTakeoverAsync(destination);

        Assert.Equal("Available", destination.RepositoryWriterStatus);
        Assert.False(destination.CanReviewStaleWriter);
        Assert.Contains("preserved as evidence", destination.RepositoryWriterDetails, StringComparison.Ordinal);
        RepositoryLeaseEvidence evidence = Assert.Single(RepositoryLeaseService.ListEvidence(repository.Path));
        Assert.Equal(oldWriter.Lease.Nonce, evidence.Nonce);
        Assert.Equal("stale-takeover", evidence.Disposition);
        Assert.False(oldWriter.IsOwner);
    }

    private sealed class FixedIdentityProvider : IInstallationIdentityProvider
    {
        public string GetOrCreate() => "fedcba0987654321fedcba0987654321";
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
