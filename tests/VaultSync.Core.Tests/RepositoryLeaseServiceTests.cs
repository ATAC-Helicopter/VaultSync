using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RepositoryLeaseServiceTests
{
    [Fact]
    public void Constructor_RejectsUnsupportedClockSkewTolerance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RepositoryLeaseService(clockSkewTolerance: TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void Inspect_EmptyRootIsInvalid()
    {
        var service = new RepositoryLeaseService();

        RepositoryLeaseInspection inspection = service.Inspect(string.Empty);

        Assert.Equal(RepositoryLeaseState.Invalid, inspection.State);
        Assert.Null(inspection.Lease);
    }

    [Fact]
    public void Inspect_MissingCoordinationDatabaseIsAvailableAndReadOnly()
    {
        using var root = new TempDirectory();
        var service = new RepositoryLeaseService();

        RepositoryLeaseInspection inspection = service.Inspect(root.Path);

        Assert.Equal(RepositoryLeaseState.Available, inspection.State);
        Assert.False(File.Exists(RepositoryLeaseService.GetDatabasePath(root.Path)));
    }

    [Fact]
    public void TryAcquire_UnavailableRootDoesNotCreateDestination()
    {
        using var parent = new TempDirectory();
        string unavailableRoot = Path.Combine(parent.Path, "offline");
        var service = new RepositoryLeaseService();

        RepositoryLeaseAcquireResult result = service.TryAcquire(
            unavailableRoot,
            CreateRequest("metadata-export"));

        Assert.Equal(RepositoryLeaseAcquireStatus.Unavailable, result.Status);
        Assert.False(Directory.Exists(unavailableRoot));
    }

    [Fact]
    public void TryAcquire_SecondWriterIsBusyAndReadOnlyInspectionRemainsAvailable()
    {
        using var root = new TempDirectory();
        var service = new RepositoryLeaseService();
        RepositoryLeaseRequest firstRequest = CreateRequest("metadata-export");
        RepositoryLeaseRequest secondRequest = CreateRequest("metadata-export");

        using RepositoryLeaseHandle first = AssertAcquired(service.TryAcquire(root.Path, firstRequest));
        RepositoryLeaseAcquireResult second = service.TryAcquire(root.Path, secondRequest);
        RepositoryLeaseInspection inspection = service.Inspect(root.Path);

        Assert.Equal(RepositoryLeaseAcquireStatus.Busy, second.Status);
        Assert.Equal(RepositoryLeaseState.Active, inspection.State);
        Assert.Equal(first.Lease.Nonce, inspection.Lease?.Nonce);
        Assert.True(first.IsOwner);
    }

    [Fact]
    public async System.Threading.Tasks.Task TryAcquire_ConcurrentWritersProduceExactlyOneOwner()
    {
        using var root = new TempDirectory();
        var service = new RepositoryLeaseService();

        RepositoryLeaseAcquireResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => service.TryAcquire(root.Path, CreateRequest("concurrent-export")))));

        RepositoryLeaseAcquireResult acquired = Assert.Single(results, result => result.Acquired);
        Assert.All(
            results.Where(result => !ReferenceEquals(result, acquired)),
            result => Assert.Equal(RepositoryLeaseAcquireStatus.Busy, result.Status));
        acquired.Handle!.Dispose();
    }

    [Fact]
    public void Renew_ExtendsOwnedLeaseAndDisposeReleasesIt()
    {
        using var root = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
        var service = new RepositoryLeaseService(clock, TimeSpan.Zero);
        RepositoryLeaseAcquireResult acquired = service.TryAcquire(root.Path, CreateRequest("long-export"));
        RepositoryLeaseHandle handle = AssertAcquired(acquired);
        DateTimeOffset firstExpiry = handle.Lease.ExpiresUtc;

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(handle.Renew());
        Assert.True(handle.Lease.ExpiresUtc > firstExpiry);

        handle.Dispose();

        Assert.Equal(RepositoryLeaseState.Available, service.Inspect(root.Path).State);
        Assert.Empty(RepositoryLeaseService.ListEvidence(root.Path));
        Assert.False(handle.Renew());
        Assert.False(handle.IsOwner);
        handle.Dispose();
    }

    [Fact]
    public void StaleLeaseRequiresExplicitNonceBoundTakeoverAndPreservesEvidence()
    {
        using var root = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
        var service = new RepositoryLeaseService(clock, TimeSpan.Zero);
        RepositoryLeaseHandle oldHandle = AssertAcquired(service.TryAcquire(root.Path, CreateRequest("metadata-export")));

        clock.Advance(TimeSpan.FromMinutes(6));
        RepositoryLeaseAcquireResult ordinaryAcquire = service.TryAcquire(root.Path, CreateRequest("metadata-export"));
        RepositoryLeaseAcquireResult wrongTakeover = service.TakeOverStale(
            root.Path,
            Guid.NewGuid().ToString("N"),
            CreateRequest("stale-takeover"));
        RepositoryLeaseAcquireResult takeover = service.TakeOverStale(
            root.Path,
            oldHandle.Lease.Nonce,
            CreateRequest("stale-takeover"));
        using RepositoryLeaseHandle replacement = AssertAcquired(takeover);

        Assert.Equal(RepositoryLeaseAcquireStatus.Stale, ordinaryAcquire.Status);
        Assert.Equal(RepositoryLeaseAcquireStatus.Invalid, wrongTakeover.Status);
        Assert.False(oldHandle.Renew());
        oldHandle.Dispose();
        Assert.True(replacement.IsOwner);
        RepositoryLeaseEvidence evidence = Assert.Single(RepositoryLeaseService.ListEvidence(root.Path));
        Assert.Equal("stale-takeover", evidence.Disposition);
        Assert.Equal(ordinaryAcquire.Inspection.Lease?.Nonce, evidence.Nonce);
        Assert.Equal(oldHandle.Lease.InstallationId, evidence.InstallationId);
        Assert.Equal(oldHandle.Lease.HostLabel, evidence.HostLabel);
        Assert.Equal(oldHandle.Lease.Operation, evidence.Operation);
        Assert.Equal(oldHandle.Lease.AppVersion, evidence.AppVersion);
        Assert.Equal(oldHandle.Lease.AcquiredUtc, evidence.AcquiredUtc);
        Assert.Equal(oldHandle.Lease.HeartbeatUtc, evidence.HeartbeatUtc);
        Assert.Equal(oldHandle.Lease.ExpiresUtc, evidence.ExpiresUtc);
        Assert.Equal(clock.GetUtcNow(), evidence.RecordedUtc);
    }

    [Fact]
    public void TryAcquire_SameInstallationWithAnotherNonceIsStillBusy()
    {
        using var root = new TempDirectory();
        var service = new RepositoryLeaseService();
        string installationId = Guid.NewGuid().ToString("N");
        RepositoryLeaseRequest firstRequest = CreateRequest("first", installationId);
        RepositoryLeaseRequest cloneRequest = CreateRequest("clone", installationId);

        using RepositoryLeaseHandle first = AssertAcquired(service.TryAcquire(root.Path, firstRequest));
        RepositoryLeaseAcquireResult clone = service.TryAcquire(root.Path, cloneRequest);

        Assert.Equal(RepositoryLeaseAcquireStatus.Busy, clone.Status);
        Assert.Equal(installationId, clone.Inspection.Lease?.InstallationId);
    }

    [Theory]
    [InlineData("machine-name")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFAB")]
    public void TryAcquire_InvalidInstallationIdentityFailsWithoutCreatingDatabase(string identity)
    {
        using var root = new TempDirectory();
        var service = new RepositoryLeaseService();

        RepositoryLeaseAcquireResult result = service.TryAcquire(
            root.Path,
            CreateRequest("metadata-export", identity));

        Assert.Equal(RepositoryLeaseAcquireStatus.Invalid, result.Status);
        Assert.False(File.Exists(RepositoryLeaseService.GetDatabasePath(root.Path)));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("version")]
    [InlineData("host")]
    [InlineData("duration")]
    public void TryAcquire_InvalidRequestFieldsFailClosed(string invalidField)
    {
        using var root = new TempDirectory();
        var service = new RepositoryLeaseService();
        RepositoryLeaseRequest request = CreateRequest("metadata-export") with
        {
            Operation = invalidField == "operation" ? string.Empty : "metadata-export",
            AppVersion = invalidField == "version" ? string.Empty : "1.8.7",
            HostLabel = invalidField == "host" ? new string('h', 201) : "Test host",
            Duration = invalidField == "duration" ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(5)
        };

        RepositoryLeaseAcquireResult result = service.TryAcquire(root.Path, request);

        Assert.Equal(RepositoryLeaseAcquireStatus.Invalid, result.Status);
        Assert.False(File.Exists(RepositoryLeaseService.GetDatabasePath(root.Path)));
    }

    [Fact]
    public void Inspect_ExistingDatabaseWithoutLeaseSchemaIsInvalid()
    {
        using var root = new TempDirectory();
        var service = new RepositoryLeaseService();
        string databasePath = RepositoryLeaseService.GetDatabasePath(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            connection.Execute("CREATE TABLE unrelated(id INTEGER PRIMARY KEY);");
        }

        RepositoryLeaseInspection inspection = service.Inspect(root.Path);

        Assert.Equal(RepositoryLeaseState.Invalid, inspection.State);
    }

    [Fact]
    public void ListEvidence_MissingDatabaseReturnsEmpty()
    {
        using var root = new TempDirectory();

        Assert.Empty(RepositoryLeaseService.ListEvidence(root.Path));
    }

    [Fact]
    public void ListEvidence_DatabaseWithoutEvidenceSchemaReturnsEmpty()
    {
        using var root = new TempDirectory();
        string databasePath = RepositoryLeaseService.GetDatabasePath(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            connection.Execute("CREATE TABLE unrelated(id INTEGER PRIMARY KEY);");
        }

        Assert.Empty(RepositoryLeaseService.ListEvidence(root.Path));
    }

    [Fact]
    public void CorruptCoordinationDatabaseFailsClosedAcrossReadAndMutationOperations()
    {
        using var root = new TempDirectory();
        string databasePath = RepositoryLeaseService.GetDatabasePath(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllText(databasePath, "not a SQLite database");
        var service = new RepositoryLeaseService();
        string installationId = Guid.NewGuid().ToString("N");
        string nonce = Guid.NewGuid().ToString("N");

        Assert.Equal(RepositoryLeaseState.Unavailable, service.Inspect(root.Path).State);
        Assert.Empty(RepositoryLeaseService.ListEvidence(root.Path));
        Assert.False(RepositoryLeaseService.TryRelease(root.Path, installationId, nonce));
        Assert.Null(service.TryRenew(root.Path, installationId, nonce, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void MissingLeaseCannotBeTakenOverRenewedOrReleased()
    {
        using var root = new TempDirectory();
        var service = new RepositoryLeaseService();
        string installationId = Guid.NewGuid().ToString("N");
        string nonce = Guid.NewGuid().ToString("N");

        RepositoryLeaseAcquireResult takeover = service.TakeOverStale(
            root.Path,
            nonce,
            CreateRequest("stale-takeover", installationId));

        Assert.Equal(RepositoryLeaseAcquireStatus.Invalid, takeover.Status);
        Assert.False(RepositoryLeaseService.TryRelease(root.Path, installationId, nonce));
        Assert.Null(service.TryRenew(root.Path, installationId, nonce, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ForeignOwnerCannotRenewOrReleaseLease()
    {
        using var root = new TempDirectory();
        var service = new RepositoryLeaseService();
        using RepositoryLeaseHandle owner = AssertAcquired(
            service.TryAcquire(root.Path, CreateRequest("metadata-export")));

        Assert.Null(service.TryRenew(
            root.Path,
            Guid.NewGuid().ToString("N"),
            owner.Lease.Nonce,
            TimeSpan.FromMinutes(5)));
        Assert.False(RepositoryLeaseService.TryRelease(
            root.Path,
            owner.Lease.InstallationId,
            Guid.NewGuid().ToString("N")));
        Assert.True(owner.IsOwner);
    }

    [Fact]
    public void ExpiredOwnerCannotRenewLease()
    {
        using var root = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));
        var service = new RepositoryLeaseService(clock, TimeSpan.Zero);
        using RepositoryLeaseHandle owner = AssertAcquired(
            service.TryAcquire(root.Path, CreateRequest("metadata-export")));

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.False(owner.Renew());
        Assert.Equal(RepositoryLeaseState.Stale, service.Inspect(root.Path).State);
    }

    [Fact]
    public void InvalidRepositoryRootFailsClosedWithoutThrowing()
    {
        var service = new RepositoryLeaseService();
        const string invalidRoot = "invalid\0root";
        string nonce = Guid.NewGuid().ToString("N");

        Assert.Equal(RepositoryLeaseState.Invalid, service.Inspect(invalidRoot).State);
        Assert.Equal(
            RepositoryLeaseAcquireStatus.Invalid,
            service.TakeOverStale(invalidRoot, nonce, CreateRequest("stale-takeover")).Status);
        Assert.Empty(RepositoryLeaseService.ListEvidence(invalidRoot));
    }

    [Fact]
    public void LinkedCoordinationDatabaseFailsClosed()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var root = new TempDirectory();
        using var target = new TempDirectory();
        string databasePath = RepositoryLeaseService.GetDatabasePath(root.Path);
        string targetPath = Path.Combine(target.Path, "coordination.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllText(targetPath, "external data");
        File.CreateSymbolicLink(databasePath, targetPath);
        var service = new RepositoryLeaseService();
        string installationId = Guid.NewGuid().ToString("N");
        string nonce = Guid.NewGuid().ToString("N");

        Assert.Equal(RepositoryLeaseState.Invalid, service.Inspect(root.Path).State);
        Assert.Equal(
            RepositoryLeaseAcquireStatus.Invalid,
            service.TryAcquire(root.Path, CreateRequest("metadata-export")).Status);
        Assert.Equal(
            RepositoryLeaseAcquireStatus.Invalid,
            service.TakeOverStale(root.Path, nonce, CreateRequest("stale-takeover")).Status);
        Assert.Empty(RepositoryLeaseService.ListEvidence(root.Path));
        Assert.False(RepositoryLeaseService.TryRelease(root.Path, installationId, nonce));
        Assert.Null(service.TryRenew(root.Path, installationId, nonce, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void TakeOverStale_InvalidNonceFailsWithoutChangingOwner()
    {
        using var root = new TempDirectory();
        var service = new RepositoryLeaseService();
        using RepositoryLeaseHandle owner = AssertAcquired(service.TryAcquire(root.Path, CreateRequest("metadata-export")));

        RepositoryLeaseAcquireResult result = service.TakeOverStale(root.Path, "invalid", CreateRequest("takeover"));

        Assert.Equal(RepositoryLeaseAcquireStatus.Invalid, result.Status);
        Assert.True(owner.IsOwner);
    }

    [Fact]
    public void Inspect_MalformedLeaseFailsClosed()
    {
        using var root = new TempDirectory();
        var service = new RepositoryLeaseService();
        using RepositoryLeaseHandle handle = AssertAcquired(service.TryAcquire(root.Path, CreateRequest("metadata-export")));
        handle.Dispose();

        using (var connection = new SqliteConnection($"Data Source={RepositoryLeaseService.GetDatabasePath(root.Path)}"))
        {
            connection.Open();
            connection.Execute(
                """
                INSERT INTO repository_lease(
                  lease_id, protocol_version, installation_id, host_label, process_id,
                  operation, nonce, app_version, acquired_utc, heartbeat_utc, expires_utc)
                VALUES(1, 999, 'bad', '', 1, 'write', 'bad', '1.8.7', 'bad', 'bad', 'bad');
                """);
        }

        RepositoryLeaseInspection inspection = service.Inspect(root.Path);
        RepositoryLeaseAcquireResult acquire = service.TryAcquire(root.Path, CreateRequest("metadata-export"));

        Assert.Equal(RepositoryLeaseState.Invalid, inspection.State);
        Assert.Equal(RepositoryLeaseAcquireStatus.Invalid, acquire.Status);
    }

    private static RepositoryLeaseRequest CreateRequest(string operation, string installationId = null) =>
        new(
            installationId ?? Guid.NewGuid().ToString("N"),
            "Test host",
            operation,
            "1.8.7",
            TimeSpan.FromMinutes(5));

    private static RepositoryLeaseHandle AssertAcquired(RepositoryLeaseAcquireResult result)
    {
        Assert.Equal(RepositoryLeaseAcquireStatus.Acquired, result.Status);
        return Assert.IsType<RepositoryLeaseHandle>(result.Handle);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }
}
