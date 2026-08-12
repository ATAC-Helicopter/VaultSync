using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace VaultSync.Core.Services;

public sealed record RepositoryLeaseRequest(
    string InstallationId,
    string HostLabel,
    string Operation,
    string AppVersion,
    TimeSpan? Duration = null);

public sealed record RepositoryLeaseSnapshot(
    int ProtocolVersion,
    string InstallationId,
    string HostLabel,
    int ProcessId,
    string Operation,
    string Nonce,
    string AppVersion,
    DateTimeOffset AcquiredUtc,
    DateTimeOffset HeartbeatUtc,
    DateTimeOffset ExpiresUtc);

public sealed record RepositoryLeaseInspection(
    RepositoryLeaseState State,
    RepositoryLeaseSnapshot? Lease,
    string Message);

public sealed record RepositoryLeaseAcquireResult(
    RepositoryLeaseAcquireStatus Status,
    RepositoryLeaseInspection Inspection,
    RepositoryLeaseHandle? Handle)
{
    public bool Acquired => Status == RepositoryLeaseAcquireStatus.Acquired && Handle is not null;
}

public sealed record RepositoryLeaseEvidence(
    string Nonce,
    string InstallationId,
    string HostLabel,
    string Operation,
    string AppVersion,
    DateTimeOffset AcquiredUtc,
    DateTimeOffset HeartbeatUtc,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset RecordedUtc,
    string Disposition);

public enum RepositoryLeaseState
{
    Available,
    Active,
    Stale,
    Invalid,
    Unavailable
}

public enum RepositoryLeaseAcquireStatus
{
    Acquired,
    Busy,
    Stale,
    Invalid,
    Unavailable
}

/// <summary>
/// Coordinates cooperating VaultSync writers through a repository-local SQLite
/// lease. The coordination database is separate from portable metadata schema
/// evolution so read-only inspection and lease rollout do not rewrite metadata.
/// </summary>
public sealed class RepositoryLeaseService
{
    public const int CurrentProtocolVersion = 1;
    public const string CoordinationDatabaseName = "writer.lease.db";

    private const int SingletonLeaseId = 1;
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DefaultClockSkewTolerance = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _clockSkewTolerance;

    public RepositoryLeaseService(
        TimeProvider? timeProvider = null,
        TimeSpan? clockSkewTolerance = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clockSkewTolerance = clockSkewTolerance ?? DefaultClockSkewTolerance;
        if (_clockSkewTolerance < TimeSpan.Zero || _clockSkewTolerance > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(clockSkewTolerance));
    }

    public string GetDatabasePath(string rootPath) =>
        Path.Combine(GetMetadataDirectory(rootPath), CoordinationDatabaseName);

    public RepositoryLeaseInspection Inspect(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return InvalidInspection("Repository root is empty.");

        string databasePath;
        try
        {
            databasePath = GetDatabasePath(rootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return InvalidInspection("Repository root is invalid.");
        }

        if (!File.Exists(databasePath))
            return AvailableInspection();

        if (IsLinkedFile(databasePath))
            return InvalidInspection("Repository coordination database must be a regular file.");

        try
        {
            using SqliteConnection connection = OpenConnection(databasePath, readOnly: true);
            if (!HasLeaseTable(connection))
                return InvalidInspection("Repository coordination database has no supported lease table.");

            return InspectInConnection(connection, transaction: null, _timeProvider.GetUtcNow());
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            return new RepositoryLeaseInspection(
                RepositoryLeaseState.Unavailable,
                null,
                $"Repository coordination state is unavailable: {ex.Message}");
        }
    }

    public RepositoryLeaseAcquireResult TryAcquire(string rootPath, RepositoryLeaseRequest request)
    {
        string? validationError = ValidateRequest(request);
        if (validationError is not null)
            return FailedAcquire(RepositoryLeaseAcquireStatus.Invalid, InvalidInspection(validationError));

        string databasePath;
        try
        {
            databasePath = GetDatabasePath(rootPath);
            if (!Directory.Exists(Path.GetFullPath(rootPath)))
            {
                return FailedAcquire(
                    RepositoryLeaseAcquireStatus.Unavailable,
                    new RepositoryLeaseInspection(
                        RepositoryLeaseState.Unavailable,
                        null,
                        "Repository root is unavailable."));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            return FailedAcquire(
                RepositoryLeaseAcquireStatus.Unavailable,
                new RepositoryLeaseInspection(RepositoryLeaseState.Unavailable, null, ex.Message));
        }

        if (File.Exists(databasePath) && IsLinkedFile(databasePath))
            return FailedAcquire(RepositoryLeaseAcquireStatus.Invalid, InvalidInspection("Repository coordination database must be a regular file."));

        try
        {
            using SqliteConnection connection = OpenConnection(databasePath, readOnly: false);
            EnsureSchema(connection);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            RepositoryLeaseInspection current = InspectInConnection(connection, transaction, now);
            if (current.State != RepositoryLeaseState.Available)
            {
                transaction.Rollback();
                return current.State switch
                {
                    RepositoryLeaseState.Active => FailedAcquire(RepositoryLeaseAcquireStatus.Busy, current),
                    RepositoryLeaseState.Stale => FailedAcquire(RepositoryLeaseAcquireStatus.Stale, current),
                    RepositoryLeaseState.Invalid => FailedAcquire(RepositoryLeaseAcquireStatus.Invalid, current),
                    _ => FailedAcquire(RepositoryLeaseAcquireStatus.Unavailable, current)
                };
            }

            RepositoryLeaseSnapshot lease = CreateSnapshot(request, now);
            InsertLease(connection, transaction, lease);
            transaction.Commit();
            return AcquiredResult(rootPath, lease, ResolveDuration(request.Duration));
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            RepositoryLeaseInspection current = Inspect(rootPath);
            if (current.State == RepositoryLeaseState.Active)
                return FailedAcquire(RepositoryLeaseAcquireStatus.Busy, current);
            if (current.State == RepositoryLeaseState.Stale)
                return FailedAcquire(RepositoryLeaseAcquireStatus.Stale, current);

            return FailedAcquire(
                RepositoryLeaseAcquireStatus.Unavailable,
                new RepositoryLeaseInspection(RepositoryLeaseState.Unavailable, null, ex.Message));
        }
    }

    public RepositoryLeaseAcquireResult TakeOverStale(
        string rootPath,
        string expectedNonce,
        RepositoryLeaseRequest request)
    {
        string? validationError = ValidateRequest(request);
        if (validationError is not null || !IsCanonicalId(expectedNonce))
        {
            return FailedAcquire(
                RepositoryLeaseAcquireStatus.Invalid,
                InvalidInspection(validationError ?? "Expected lease nonce is invalid."));
        }

        string databasePath;
        try
        {
            databasePath = GetDatabasePath(rootPath);
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            return FailedAcquire(RepositoryLeaseAcquireStatus.Invalid, InvalidInspection("Repository root is invalid."));
        }
        if (!File.Exists(databasePath) || IsLinkedFile(databasePath))
            return FailedAcquire(RepositoryLeaseAcquireStatus.Invalid, InvalidInspection("No valid stale lease is available for takeover."));

        try
        {
            using SqliteConnection connection = OpenConnection(databasePath, readOnly: false);
            EnsureSchema(connection);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            RepositoryLeaseInspection current = InspectInConnection(connection, transaction, now);
            if (current.State != RepositoryLeaseState.Stale ||
                current.Lease is null ||
                !string.Equals(current.Lease.Nonce, expectedNonce, StringComparison.Ordinal))
            {
                transaction.Rollback();
                RepositoryLeaseAcquireStatus status = current.State == RepositoryLeaseState.Active
                    ? RepositoryLeaseAcquireStatus.Busy
                    : RepositoryLeaseAcquireStatus.Invalid;
                return FailedAcquire(status, current);
            }

            RecordEvidence(connection, transaction, current.Lease, now, "stale-takeover");
            RepositoryLeaseSnapshot replacement = CreateSnapshot(request, now);
            int changed = ReplaceLease(connection, transaction, expectedNonce, replacement);
            if (changed != 1)
            {
                transaction.Rollback();
                return FailedAcquire(RepositoryLeaseAcquireStatus.Busy, Inspect(rootPath));
            }

            transaction.Commit();
            return AcquiredResult(rootPath, replacement, ResolveDuration(request.Duration));
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            return FailedAcquire(
                RepositoryLeaseAcquireStatus.Unavailable,
                new RepositoryLeaseInspection(RepositoryLeaseState.Unavailable, null, ex.Message));
        }
    }

    public IReadOnlyList<RepositoryLeaseEvidence> ListEvidence(string rootPath)
    {
        string databasePath;
        try
        {
            databasePath = GetDatabasePath(rootPath);
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            return [];
        }
        if (!File.Exists(databasePath) || IsLinkedFile(databasePath))
            return [];

        try
        {
            using SqliteConnection connection = OpenConnection(databasePath, readOnly: true);
            if (!HasEvidenceTable(connection))
                return [];

            return connection.Query<LeaseEvidenceRow>(
                    """
                    SELECT
                      nonce as Nonce,
                      installation_id as InstallationId,
                      host_label as HostLabel,
                      operation as Operation,
                      app_version as AppVersion,
                      acquired_utc as AcquiredUtc,
                      heartbeat_utc as HeartbeatUtc,
                      expires_utc as ExpiresUtc,
                      recorded_utc as RecordedUtc,
                      disposition as Disposition
                    FROM lease_evidence
                    ORDER BY evidence_id;
                    """)
                .Select(ToEvidence)
                .ToList();
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            return [];
        }
    }

    internal RepositoryLeaseSnapshot? TryRenew(
        string rootPath,
        string installationId,
        string nonce,
        TimeSpan duration)
    {
        return MutateOwnedLease(
            rootPath,
            installationId,
            nonce,
            (connection, transaction, current, now) =>
            {
                if (current.ExpiresUtc <= now)
                    return null;

                RepositoryLeaseSnapshot renewed = current with
                {
                    HeartbeatUtc = now,
                    ExpiresUtc = now.Add(duration)
                };
                int changed = UpdateHeartbeat(connection, transaction, renewed);
                return changed == 1 ? renewed : null;
            });
    }

    internal bool IsOwner(string rootPath, string installationId, string nonce)
    {
        RepositoryLeaseInspection inspection = Inspect(rootPath);
        return inspection.State == RepositoryLeaseState.Active &&
               inspection.Lease is not null &&
               string.Equals(inspection.Lease.InstallationId, installationId, StringComparison.Ordinal) &&
               string.Equals(inspection.Lease.Nonce, nonce, StringComparison.Ordinal);
    }

    internal bool TryRelease(string rootPath, string installationId, string nonce)
    {
        string databasePath = GetDatabasePath(rootPath);
        if (!File.Exists(databasePath) || IsLinkedFile(databasePath))
            return false;

        try
        {
            using SqliteConnection connection = OpenConnection(databasePath, readOnly: false);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            LeaseRow? row = QueryLease(connection, transaction);
            if (!TryParse(row, out RepositoryLeaseSnapshot? current, out _) ||
                current is null ||
                !string.Equals(current.InstallationId, installationId, StringComparison.Ordinal) ||
                !string.Equals(current.Nonce, nonce, StringComparison.Ordinal))
            {
                transaction.Rollback();
                return false;
            }

            int changed = connection.Execute(
                "DELETE FROM repository_lease WHERE lease_id = @LeaseId AND installation_id = @InstallationId AND nonce = @Nonce;",
                new
                {
                    LeaseId = SingletonLeaseId,
                    InstallationId = installationId,
                    Nonce = nonce
                },
                transaction);
            transaction.Commit();
            return changed == 1;
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            return false;
        }
    }

    private RepositoryLeaseSnapshot? MutateOwnedLease(
        string rootPath,
        string installationId,
        string nonce,
        Func<SqliteConnection, SqliteTransaction, RepositoryLeaseSnapshot, DateTimeOffset, RepositoryLeaseSnapshot?> mutation)
    {
        string databasePath = GetDatabasePath(rootPath);
        if (!File.Exists(databasePath) || IsLinkedFile(databasePath))
            return null;

        try
        {
            using SqliteConnection connection = OpenConnection(databasePath, readOnly: false);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            LeaseRow? row = QueryLease(connection, transaction);
            if (!TryParse(row, out RepositoryLeaseSnapshot? current, out _) ||
                current is null ||
                !string.Equals(current.InstallationId, installationId, StringComparison.Ordinal) ||
                !string.Equals(current.Nonce, nonce, StringComparison.Ordinal))
            {
                transaction.Rollback();
                return null;
            }

            RepositoryLeaseSnapshot? updated = mutation(connection, transaction, current, _timeProvider.GetUtcNow());
            if (updated is null)
            {
                transaction.Rollback();
                return null;
            }

            transaction.Commit();
            return updated;
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            return null;
        }
    }

    private RepositoryLeaseAcquireResult AcquiredResult(
        string rootPath,
        RepositoryLeaseSnapshot lease,
        TimeSpan duration)
    {
        var inspection = new RepositoryLeaseInspection(RepositoryLeaseState.Active, lease, "Repository write lease acquired.");
        return new RepositoryLeaseAcquireResult(
            RepositoryLeaseAcquireStatus.Acquired,
            inspection,
            new RepositoryLeaseHandle(this, rootPath, lease, duration));
    }

    internal ITimer CreateHeartbeatTimer(TimerCallback callback, object state, TimeSpan interval) =>
        _timeProvider.CreateTimer(callback, state, interval, interval);

    private RepositoryLeaseSnapshot CreateSnapshot(RepositoryLeaseRequest request, DateTimeOffset now)
    {
        TimeSpan duration = ResolveDuration(request.Duration);
        return new RepositoryLeaseSnapshot(
            CurrentProtocolVersion,
            request.InstallationId,
            request.HostLabel.Trim(),
            Environment.ProcessId,
            request.Operation.Trim(),
            Guid.NewGuid().ToString("N"),
            request.AppVersion.Trim(),
            now,
            now,
            now.Add(duration));
    }

    private RepositoryLeaseInspection InspectInConnection(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DateTimeOffset now)
    {
        LeaseRow? row = QueryLease(connection, transaction);
        if (row is null)
            return AvailableInspection();

        if (!TryParse(row, out RepositoryLeaseSnapshot? lease, out string error) || lease is null)
            return InvalidInspection(error);

        bool stale = lease.ExpiresUtc.Add(_clockSkewTolerance) <= now;
        return stale
            ? new RepositoryLeaseInspection(RepositoryLeaseState.Stale, lease, "Repository write lease is stale and requires explicit takeover.")
            : new RepositoryLeaseInspection(RepositoryLeaseState.Active, lease, "Repository is busy; read-only inspection remains available.");
    }

    private static LeaseRow? QueryLease(SqliteConnection connection, SqliteTransaction? transaction) =>
        connection.QuerySingleOrDefault<LeaseRow>(
            """
            SELECT
              protocol_version as ProtocolVersion,
              installation_id as InstallationId,
              host_label as HostLabel,
              process_id as ProcessId,
              operation as Operation,
              nonce as Nonce,
              app_version as AppVersion,
              acquired_utc as AcquiredUtc,
              heartbeat_utc as HeartbeatUtc,
              expires_utc as ExpiresUtc
            FROM repository_lease
            WHERE lease_id = @LeaseId;
            """,
            new
            {
                LeaseId = SingletonLeaseId
            },
            transaction);

    private static void InsertLease(SqliteConnection connection, SqliteTransaction transaction, RepositoryLeaseSnapshot lease)
    {
        connection.Execute(
            """
            INSERT INTO repository_lease(
              lease_id, protocol_version, installation_id, host_label, process_id,
              operation, nonce, app_version, acquired_utc, heartbeat_utc, expires_utc)
            VALUES(
              @LeaseId, @ProtocolVersion, @InstallationId, @HostLabel, @ProcessId,
              @Operation, @Nonce, @AppVersion, @AcquiredUtc, @HeartbeatUtc, @ExpiresUtc);
            """,
            ToParameters(lease),
            transaction);
    }

    private static int ReplaceLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string expectedNonce,
        RepositoryLeaseSnapshot lease)
    {
        var parameters = ToParameters(lease);
        return connection.Execute(
            """
            UPDATE repository_lease SET
              protocol_version = @ProtocolVersion,
              installation_id = @InstallationId,
              host_label = @HostLabel,
              process_id = @ProcessId,
              operation = @Operation,
              nonce = @Nonce,
              app_version = @AppVersion,
              acquired_utc = @AcquiredUtc,
              heartbeat_utc = @HeartbeatUtc,
              expires_utc = @ExpiresUtc
            WHERE lease_id = @LeaseId AND nonce = @ExpectedNonce;
            """,
            new
            {
                parameters.LeaseId,
                parameters.ProtocolVersion,
                parameters.InstallationId,
                parameters.HostLabel,
                parameters.ProcessId,
                parameters.Operation,
                parameters.Nonce,
                parameters.AppVersion,
                parameters.AcquiredUtc,
                parameters.HeartbeatUtc,
                parameters.ExpiresUtc,
                ExpectedNonce = expectedNonce
            },
            transaction);
    }

    private static int UpdateHeartbeat(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryLeaseSnapshot lease) =>
        connection.Execute(
            """
            UPDATE repository_lease
            SET heartbeat_utc = @HeartbeatUtc, expires_utc = @ExpiresUtc
            WHERE lease_id = @LeaseId AND installation_id = @InstallationId AND nonce = @Nonce;
            """,
            new
            {
                LeaseId = SingletonLeaseId,
                lease.InstallationId,
                lease.Nonce,
                HeartbeatUtc = FormatUtc(lease.HeartbeatUtc),
                ExpiresUtc = FormatUtc(lease.ExpiresUtc)
            },
            transaction);

    private static void RecordEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryLeaseSnapshot lease,
        DateTimeOffset recordedUtc,
        string disposition)
    {
        connection.Execute(
            """
            INSERT INTO lease_evidence(
              nonce, installation_id, host_label, operation, app_version,
              acquired_utc, heartbeat_utc, expires_utc, recorded_utc, disposition)
            VALUES(
              @Nonce, @InstallationId, @HostLabel, @Operation, @AppVersion,
              @AcquiredUtc, @HeartbeatUtc, @ExpiresUtc, @RecordedUtc, @Disposition);
            """,
            new
            {
                lease.Nonce,
                lease.InstallationId,
                lease.HostLabel,
                lease.Operation,
                lease.AppVersion,
                AcquiredUtc = FormatUtc(lease.AcquiredUtc),
                HeartbeatUtc = FormatUtc(lease.HeartbeatUtc),
                ExpiresUtc = FormatUtc(lease.ExpiresUtc),
                RecordedUtc = FormatUtc(recordedUtc),
                Disposition = disposition
            },
            transaction);
    }

    private static LeaseParameters ToParameters(RepositoryLeaseSnapshot lease) => new()
    {
        LeaseId = SingletonLeaseId,
        ProtocolVersion = lease.ProtocolVersion,
        InstallationId = lease.InstallationId,
        HostLabel = lease.HostLabel,
        ProcessId = lease.ProcessId,
        Operation = lease.Operation,
        Nonce = lease.Nonce,
        AppVersion = lease.AppVersion,
        AcquiredUtc = FormatUtc(lease.AcquiredUtc),
        HeartbeatUtc = FormatUtc(lease.HeartbeatUtc),
        ExpiresUtc = FormatUtc(lease.ExpiresUtc)
    };

    private static bool TryParse(
        LeaseRow? row,
        out RepositoryLeaseSnapshot? lease,
        out string error)
    {
        lease = null;
        if (row is null)
        {
            error = string.Empty;
            return false;
        }

        if (row.ProtocolVersion != CurrentProtocolVersion ||
            !IsCanonicalId(row.InstallationId) ||
            !IsCanonicalId(row.Nonce) ||
            string.IsNullOrWhiteSpace(row.Operation) ||
            string.IsNullOrWhiteSpace(row.AppVersion) ||
            !TryParseUtc(row.AcquiredUtc, out DateTimeOffset acquiredUtc) ||
            !TryParseUtc(row.HeartbeatUtc, out DateTimeOffset heartbeatUtc) ||
            !TryParseUtc(row.ExpiresUtc, out DateTimeOffset expiresUtc) ||
            heartbeatUtc < acquiredUtc ||
            expiresUtc <= heartbeatUtc)
        {
            error = "Repository write lease is malformed or uses an unsupported protocol.";
            return false;
        }

        lease = new RepositoryLeaseSnapshot(
            row.ProtocolVersion,
            row.InstallationId,
            row.HostLabel ?? string.Empty,
            row.ProcessId,
            row.Operation,
            row.Nonce,
            row.AppVersion,
            acquiredUtc,
            heartbeatUtc,
            expiresUtc);
        error = string.Empty;
        return true;
    }

    private static RepositoryLeaseEvidence ToEvidence(LeaseEvidenceRow row) => new(
        row.Nonce,
        row.InstallationId,
        row.HostLabel ?? string.Empty,
        row.Operation,
        row.AppVersion,
        ParseUtc(row.AcquiredUtc),
        ParseUtc(row.HeartbeatUtc),
        ParseUtc(row.ExpiresUtc),
        ParseUtc(row.RecordedUtc),
        row.Disposition);

    private static string? ValidateRequest(RepositoryLeaseRequest request)
    {
        if (!IsCanonicalId(request.InstallationId))
            return "Installation identity must be a canonical non-empty identifier.";
        if (string.IsNullOrWhiteSpace(request.Operation) || request.Operation.Trim().Length > 100)
            return "Repository operation is required and must not exceed 100 characters.";
        if (string.IsNullOrWhiteSpace(request.AppVersion) || request.AppVersion.Trim().Length > 64)
            return "Application version is required and must not exceed 64 characters.";
        if (request.HostLabel is null || request.HostLabel.Trim().Length > 200)
            return "Host label must not exceed 200 characters.";

        try
        {
            _ = ResolveDuration(request.Duration);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "Lease duration is outside the supported range.";
        }

        return null;
    }

    private static TimeSpan ResolveDuration(TimeSpan? requested)
    {
        TimeSpan duration = requested ?? DefaultLeaseDuration;
        if (duration < MinimumLeaseDuration || duration > MaximumLeaseDuration)
            throw new ArgumentOutOfRangeException(nameof(requested));
        return duration;
    }

    private static bool IsCanonicalId(string? value) =>
        value is not null &&
        Guid.TryParseExact(value, "N", out Guid parsed) &&
        parsed != Guid.Empty &&
        string.Equals(value, parsed.ToString("N"), StringComparison.Ordinal);

    private static string GetMetadataDirectory(string rootPath) =>
        Path.Combine(Path.GetFullPath(rootPath), ".vaultsync", "meta");

    private static SqliteConnection OpenConnection(string databasePath, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        connection.Execute("PRAGMA busy_timeout = 5000;");
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection) => connection.Execute(
        """
        CREATE TABLE IF NOT EXISTS repository_lease(
          lease_id INTEGER PRIMARY KEY CHECK(lease_id = 1),
          protocol_version INTEGER NOT NULL,
          installation_id TEXT NOT NULL,
          host_label TEXT NOT NULL,
          process_id INTEGER NOT NULL,
          operation TEXT NOT NULL,
          nonce TEXT NOT NULL,
          app_version TEXT NOT NULL,
          acquired_utc TEXT NOT NULL,
          heartbeat_utc TEXT NOT NULL,
          expires_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS lease_evidence(
          evidence_id INTEGER PRIMARY KEY AUTOINCREMENT,
          nonce TEXT NOT NULL,
          installation_id TEXT NOT NULL,
          host_label TEXT NOT NULL,
          operation TEXT NOT NULL,
          app_version TEXT NOT NULL,
          acquired_utc TEXT NOT NULL,
          heartbeat_utc TEXT NOT NULL,
          expires_utc TEXT NOT NULL,
          recorded_utc TEXT NOT NULL,
          disposition TEXT NOT NULL
        );
        """);

    private static bool HasLeaseTable(SqliteConnection connection) =>
        connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'repository_lease';") == 1;

    private static bool HasEvidenceTable(SqliteConnection connection) =>
        connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'lease_evidence';") == 1;

    private static bool IsLinkedFile(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            return true;
        }
    }

    private static bool IsStorageException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SqliteException or ArgumentException or NotSupportedException or PathTooLongException;

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool TryParseUtc(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);

    private static DateTimeOffset ParseUtc(string value) =>
        TryParseUtc(value, out DateTimeOffset parsed)
            ? parsed
            : throw new InvalidDataException("Repository lease evidence timestamp is malformed.");

    private static RepositoryLeaseInspection AvailableInspection() =>
        new(RepositoryLeaseState.Available, null, "Repository has no active writer.");

    private static RepositoryLeaseInspection InvalidInspection(string message) =>
        new(RepositoryLeaseState.Invalid, null, message);

    private static RepositoryLeaseAcquireResult FailedAcquire(
        RepositoryLeaseAcquireStatus status,
        RepositoryLeaseInspection inspection) =>
        new(status, inspection, null);

    private sealed class LeaseRow
    {
        public int ProtocolVersion
        {
            get; set;
        }
        public string InstallationId { get; set; } = string.Empty;
        public string? HostLabel
        {
            get; set;
        }
        public int ProcessId
        {
            get; set;
        }
        public string Operation { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string AppVersion { get; set; } = string.Empty;
        public string AcquiredUtc { get; set; } = string.Empty;
        public string HeartbeatUtc { get; set; } = string.Empty;
        public string ExpiresUtc { get; set; } = string.Empty;
    }

    private sealed class LeaseParameters
    {
        public int LeaseId
        {
            get; set;
        }
        public int ProtocolVersion
        {
            get; set;
        }
        public string InstallationId { get; set; } = string.Empty;
        public string HostLabel { get; set; } = string.Empty;
        public int ProcessId
        {
            get; set;
        }
        public string Operation { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string AppVersion { get; set; } = string.Empty;
        public string AcquiredUtc { get; set; } = string.Empty;
        public string HeartbeatUtc { get; set; } = string.Empty;
        public string ExpiresUtc { get; set; } = string.Empty;
    }

    private sealed class LeaseEvidenceRow
    {
        public string Nonce { get; set; } = string.Empty;
        public string InstallationId { get; set; } = string.Empty;
        public string? HostLabel
        {
            get; set;
        }
        public string Operation { get; set; } = string.Empty;
        public string AppVersion { get; set; } = string.Empty;
        public string AcquiredUtc { get; set; } = string.Empty;
        public string HeartbeatUtc { get; set; } = string.Empty;
        public string ExpiresUtc { get; set; } = string.Empty;
        public string RecordedUtc { get; set; } = string.Empty;
        public string Disposition { get; set; } = string.Empty;
    }
}

public sealed class RepositoryLeaseHandle : IDisposable
{
    private readonly RepositoryLeaseService _service;
    private readonly string _rootPath;
    private readonly TimeSpan _duration;
    private readonly ITimer _heartbeatTimer;
    private int _disposed;

    internal RepositoryLeaseHandle(
        RepositoryLeaseService service,
        string rootPath,
        RepositoryLeaseSnapshot lease,
        TimeSpan duration)
    {
        _service = service;
        _rootPath = rootPath;
        Lease = lease;
        _duration = duration;
        TimeSpan heartbeatInterval = TimeSpan.FromTicks(Math.Max(1, duration.Ticks / 3));
        _heartbeatTimer = _service.CreateHeartbeatTimer(
            static state => ((RepositoryLeaseHandle)state!).Renew(),
            this,
            heartbeatInterval);
    }

    public RepositoryLeaseSnapshot Lease
    {
        get; private set;
    }

    public bool IsOwner =>
        Volatile.Read(ref _disposed) == 0 &&
        _service.IsOwner(_rootPath, Lease.InstallationId, Lease.Nonce);

    public bool Renew()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        RepositoryLeaseSnapshot? renewed = _service.TryRenew(
            _rootPath,
            Lease.InstallationId,
            Lease.Nonce,
            _duration);
        if (renewed is null)
            return false;

        Lease = renewed;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _heartbeatTimer.Dispose();
        _service.TryRelease(_rootPath, Lease.InstallationId, Lease.Nonce);
    }
}
