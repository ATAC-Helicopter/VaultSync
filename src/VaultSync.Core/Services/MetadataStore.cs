using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class MetadataStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _dbPath;

    public MetadataStore(string rootPath)
    {
        var metaRoot = Path.Combine(rootPath, ".vaultsync", "meta");
        _dbPath = Path.Combine(metaRoot, "vaultsync.meta.db");
    }

    public string DatabasePath => _dbPath;

    public void EnsureSchema()
    {
        using var c = Open(write: true);
        c.Execute("""
            CREATE TABLE IF NOT EXISTS meta_info(
              schema_version INTEGER NOT NULL,
              created_utc TEXT NOT NULL,
              last_write_utc TEXT NOT NULL,
              writer_app_version TEXT NOT NULL,
              writer_machine_id TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS projects(
              external_id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              preset TEXT NOT NULL,
              root_path_hint TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              settings_json TEXT NOT NULL,
              updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS snapshots(
              external_id TEXT PRIMARY KEY,
              project_external_id TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              file_count INTEGER NOT NULL,
              total_bytes INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS backups(
              external_id TEXT PRIMARY KEY,
              project_external_id TEXT NOT NULL,
              snapshot_external_id TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              type TEXT NOT NULL,
              total_bytes INTEGER NOT NULL,
              path_rel TEXT NOT NULL,
              destination_alias TEXT NOT NULL,
              origin_machine_name TEXT NOT NULL DEFAULT '',
              is_protected INTEGER NOT NULL DEFAULT 0,
              enc_flag INTEGER NOT NULL DEFAULT 0,
              kdf_params_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tombstones(
              entity_type TEXT NOT NULL,
              entity_id TEXT NOT NULL,
              deleted_utc TEXT NOT NULL,
              origin_machine_id TEXT NOT NULL,
              PRIMARY KEY(entity_type, entity_id)
            );

            CREATE INDEX IF NOT EXISTS idx_projects_name ON projects(name);
            CREATE INDEX IF NOT EXISTS idx_snapshots_project ON snapshots(project_external_id);
            CREATE INDEX IF NOT EXISTS idx_backups_project ON backups(project_external_id);
        """);

        try
        {
            c.Execute("ALTER TABLE backups ADD COLUMN origin_machine_name TEXT NOT NULL DEFAULT '';");
        }
        catch
        {
            // Column exists; ignore.
        }
    }

    public MetaInfo? GetMetaInfo()
    {
        using var c = TryOpenRead();
        return SafeQueryFirstOrDefault<MetaInfo>(
            c,
            """
            SELECT
              schema_version as SchemaVersion,
              created_utc as CreatedUtc,
              last_write_utc as LastWriteUtc,
              writer_app_version as WriterAppVersion,
              writer_machine_id as WriterMachineId
            FROM meta_info
            LIMIT 1;
            """);
    }

    public void UpsertMetaInfo(MetaInfo info)
    {
        using var c = Open(write: true);
        c.Execute("DELETE FROM meta_info;");
        c.Execute(
            """
            INSERT INTO meta_info(schema_version, created_utc, last_write_utc, writer_app_version, writer_machine_id)
            VALUES(@SchemaVersion, @CreatedUtc, @LastWriteUtc, @WriterAppVersion, @WriterMachineId);
            """,
            new
            {
                info.SchemaVersion,
                CreatedUtc = ToUtcString(info.CreatedUtc),
                LastWriteUtc = ToUtcString(info.LastWriteUtc),
                info.WriterAppVersion,
                info.WriterMachineId
            });
    }

    public void UpsertProject(MetaProject project)
    {
        using var c = Open(write: true);
        c.Execute(
            """
            INSERT INTO projects(external_id, name, preset, root_path_hint, created_utc, settings_json, updated_utc)
            VALUES(@ExternalId, @Name, @Preset, @RootPathHint, @CreatedUtc, @SettingsJson, @UpdatedUtc)
            ON CONFLICT(external_id) DO UPDATE SET
              name = excluded.name,
              preset = excluded.preset,
              root_path_hint = excluded.root_path_hint,
              settings_json = excluded.settings_json,
              updated_utc = excluded.updated_utc;
            """,
            new
            {
                project.ExternalId,
                project.Name,
                project.Preset,
                project.RootPathHint,
                CreatedUtc = ToUtcString(project.CreatedUtc),
                project.SettingsJson,
                UpdatedUtc = ToUtcString(project.UpdatedUtc)
            });
    }

    public void UpsertSnapshot(MetaSnapshot snapshot)
    {
        using var c = Open(write: true);
        c.Execute(
            """
            INSERT INTO snapshots(external_id, project_external_id, created_utc, file_count, total_bytes)
            VALUES(@ExternalId, @ProjectExternalId, @CreatedUtc, @FileCount, @TotalBytes)
            ON CONFLICT(external_id) DO UPDATE SET
              project_external_id = excluded.project_external_id,
              created_utc = excluded.created_utc,
              file_count = excluded.file_count,
              total_bytes = excluded.total_bytes;
            """,
            new
            {
                snapshot.ExternalId,
                snapshot.ProjectExternalId,
                CreatedUtc = ToUtcString(snapshot.CreatedUtc),
                snapshot.FileCount,
                snapshot.TotalBytes
            });
    }

    public void UpsertBackup(MetaBackup backup)
    {
        var descriptor = BackupCryptoDescriptor.FromMetadata(backup.IsEncrypted, backup.KdfParamsJson);
        var descriptorJson = descriptor.ToMetadataJson(backup.IsEncrypted);

        using var c = Open(write: true);
        c.Execute(
            """
            INSERT INTO backups(external_id, project_external_id, snapshot_external_id, created_utc, type, total_bytes, path_rel, destination_alias, origin_machine_name, is_protected, enc_flag, kdf_params_json)
            VALUES(@ExternalId, @ProjectExternalId, @SnapshotExternalId, @CreatedUtc, @Type, @TotalBytes, @PathRel, @DestinationAlias, @OriginMachineName, @IsProtected, @EncFlag, @KdfParamsJson)
            ON CONFLICT(external_id) DO UPDATE SET
              project_external_id = excluded.project_external_id,
              snapshot_external_id = excluded.snapshot_external_id,
              created_utc = excluded.created_utc,
              type = excluded.type,
              total_bytes = excluded.total_bytes,
              path_rel = excluded.path_rel,
              destination_alias = excluded.destination_alias,
              origin_machine_name = excluded.origin_machine_name,
              is_protected = excluded.is_protected,
              enc_flag = excluded.enc_flag,
              kdf_params_json = excluded.kdf_params_json;
            """,
            new
            {
                backup.ExternalId,
                backup.ProjectExternalId,
                backup.SnapshotExternalId,
                CreatedUtc = ToUtcString(backup.CreatedUtc),
                backup.Type,
                backup.TotalBytes,
                backup.PathRel,
                backup.DestinationAlias,
                backup.OriginMachineName,
                IsProtected = backup.IsProtected ? 1 : 0,
                EncFlag = backup.IsEncrypted ? 1 : 0,
                KdfParamsJson = descriptorJson
            });
    }

    public void AddTombstone(MetaTombstone tombstone)
    {
        using var c = Open(write: true);
        c.Execute(
            """
            INSERT INTO tombstones(entity_type, entity_id, deleted_utc, origin_machine_id)
            VALUES(@EntityType, @EntityId, @DeletedUtc, @OriginMachineId)
            ON CONFLICT(entity_type, entity_id) DO NOTHING;
            """,
            new
            {
                tombstone.EntityType,
                tombstone.EntityId,
                DeletedUtc = ToUtcString(tombstone.DeletedUtc),
                tombstone.OriginMachineId
            });
    }

    public IEnumerable<MetaProject> ListProjects()
    {
        using var c = TryOpenRead();
        return SafeQuery<MetaProject>(
            c,
            """
            SELECT
              external_id as ExternalId,
              name,
              preset,
              root_path_hint as RootPathHint,
              created_utc as CreatedUtc,
              settings_json as SettingsJson,
              updated_utc as UpdatedUtc
            FROM projects;
            """);
    }

    public IEnumerable<MetaProjectRef> ListProjectRefs()
    {
        using var c = TryOpenRead();
        return SafeQuery<MetaProjectRef>(
            c,
            """
            SELECT
              external_id as ExternalId,
              name as Name
            FROM projects;
            """);
    }

    public bool HasProject(string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return false;

        using var c = TryOpenRead();
        return SafeExecuteScalarInt(
            c,
            "SELECT 1 FROM projects WHERE external_id = @id LIMIT 1;",
            new { id = externalId }) == 1;
    }

    public IEnumerable<MetaSnapshot> ListSnapshots()
    {
        using var c = TryOpenRead();
        return SafeQuery<MetaSnapshot>(
            c,
            """
            SELECT
              external_id as ExternalId,
              project_external_id as ProjectExternalId,
              created_utc as CreatedUtc,
              file_count as FileCount,
              total_bytes as TotalBytes
            FROM snapshots;
            """);
    }

    public IEnumerable<MetaSnapshotRef> ListSnapshotRefs()
    {
        using var c = TryOpenRead();
        return SafeQuery<MetaSnapshotRef>(
            c,
            """
            SELECT
              external_id as ExternalId,
              project_external_id as ProjectExternalId
            FROM snapshots;
            """);
    }

    public IEnumerable<MetaBackup> ListBackups()
    {
        using var c = TryOpenRead();
        try
        {
            return SafeQuery<MetaBackup>(
                c,
                """
                SELECT
                  external_id as ExternalId,
                  project_external_id as ProjectExternalId,
                  snapshot_external_id as SnapshotExternalId,
                  created_utc as CreatedUtc,
                  type,
                  total_bytes as TotalBytes,
                  path_rel as PathRel,
                  destination_alias as DestinationAlias,
                  origin_machine_name as OriginMachineName,
                  is_protected as IsProtected,
                  enc_flag as IsEncrypted,
                  kdf_params_json as KdfParamsJson
                FROM backups;
                """);
        }
        catch
        {
            return SafeQuery<MetaBackup>(
                c,
                """
                SELECT
                  external_id as ExternalId,
                  project_external_id as ProjectExternalId,
                  snapshot_external_id as SnapshotExternalId,
                  created_utc as CreatedUtc,
                  type,
                  total_bytes as TotalBytes,
                  path_rel as PathRel,
                  destination_alias as DestinationAlias,
                  is_protected as IsProtected,
                  enc_flag as IsEncrypted,
                  kdf_params_json as KdfParamsJson
                FROM backups;
                """);
        }
    }

    public IEnumerable<MetaBackupRef> ListBackupRefs()
    {
        using var c = TryOpenRead();
        return SafeQuery<MetaBackupRef>(
            c,
            """
            SELECT
              external_id as ExternalId,
              project_external_id as ProjectExternalId
            FROM backups;
            """);
    }

    public IEnumerable<MetaTombstone> ListTombstones()
    {
        using var c = TryOpenRead();
        return SafeQuery<MetaTombstone>(
            c,
            """
            SELECT
              entity_type as EntityType,
              entity_id as EntityId,
              deleted_utc as DeletedUtc,
              origin_machine_id as OriginMachineId
            FROM tombstones;
            """);
    }

    public IEnumerable<MetaTombstoneRef> ListTombstoneRefs()
    {
        using var c = TryOpenRead();
        return SafeQuery<MetaTombstoneRef>(
            c,
            """
            SELECT
              entity_type as EntityType,
              entity_id as EntityId
            FROM tombstones;
            """);
    }

    private SqliteConnection Open(bool write)
    {
        var attempts = IsLikelyNetworkPath(_dbPath) ? 3 : 1;
        var delayMs = 200;
        Exception? lastError = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                return OpenCore(write);
            }
            catch (SqliteException ex)
            {
                lastError = ex;
            }
            catch (IOException ex)
            {
                lastError = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
            }

            if (attempt < attempts - 1)
            {
                Thread.Sleep(delayMs);
                delayMs *= 2;
            }
        }

        throw lastError ?? new InvalidOperationException("Failed to open SQLite connection.");
    }

    private SqliteConnection OpenCore(bool write)
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (write && !string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = write ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 10
        };

        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        ConfigureConnection(conn, write);
        try
        {
            var timeoutMs = IsLikelyNetworkPath(_dbPath) ? 10000 : 5000;
            conn.Execute($"PRAGMA busy_timeout = {timeoutMs};");
        }
        catch
        {
            // Best-effort; ignore pragma failures.
        }
        return conn;
    }

    private SqliteConnection? TryOpenRead()
    {
        try
        {
            if (!File.Exists(_dbPath))
                return null;

            return Open(write: false);
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<T> SafeQuery<T>(SqliteConnection? connection, string sql, object? param = null)
    {
        if (connection is null)
            return Array.Empty<T>();

        try
        {
            return connection.Query<T>(sql, param);
        }
        catch (SqliteException)
        {
            return Array.Empty<T>();
        }
        catch (IOException)
        {
            return Array.Empty<T>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<T>();
        }
    }

    private static T? SafeQueryFirstOrDefault<T>(SqliteConnection? connection, string sql, object? param = null)
    {
        if (connection is null)
            return default;

        try
        {
            return connection.QueryFirstOrDefault<T>(sql, param);
        }
        catch (SqliteException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static int SafeExecuteScalarInt(SqliteConnection? connection, string sql, object? param = null)
    {
        if (connection is null)
            return 0;

        try
        {
            return connection.ExecuteScalar<int>(sql, param);
        }
        catch (SqliteException)
        {
            return 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void ConfigureConnection(SqliteConnection conn, bool write)
    {
        if (!write)
            return;

        try
        {
            if (IsLikelyNetworkPath(_dbPath))
            {
                _ = conn.ExecuteScalar<string>("PRAGMA journal_mode = DELETE;");
                conn.Execute("PRAGMA synchronous = NORMAL;");
            }
            else
            {
                _ = conn.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");
            }
        }
        catch
        {
            // Best-effort; ignore pragma failures.
        }
    }

    private static bool IsLikelyNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith("//", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("\\\\", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.Contains("/Library/Application Support/VaultSync/mounts/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string ToUtcString(DateTime utc) =>
        utc.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
}

public sealed class MetaInfo
{
    public int SchemaVersion { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string WriterAppVersion { get; set; } = string.Empty;
    public string WriterMachineId { get; set; } = string.Empty;
}

public sealed class MetaProject
{
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Preset { get; set; } = string.Empty;
    public string RootPathHint { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string SettingsJson { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class MetaSnapshot
{
    public string ExternalId { get; set; } = string.Empty;
    public string ProjectExternalId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class MetaBackup
{
    public string ExternalId { get; set; } = string.Empty;
    public string ProjectExternalId { get; set; } = string.Empty;
    public string SnapshotExternalId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string Type { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public string PathRel { get; set; } = string.Empty;
    public string DestinationAlias { get; set; } = string.Empty;
    public string OriginMachineName { get; set; } = string.Empty;
    public bool IsProtected { get; set; }
    public bool IsEncrypted { get; set; }
    public string KdfParamsJson { get; set; } = string.Empty;
}

public sealed class MetaTombstone
{
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public DateTime DeletedUtc { get; set; }
    public string OriginMachineId { get; set; } = string.Empty;
}

public sealed class MetaProjectRef
{
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class MetaSnapshotRef
{
    public string ExternalId { get; set; } = string.Empty;
    public string ProjectExternalId { get; set; } = string.Empty;
}

public sealed class MetaBackupRef
{
    public string ExternalId { get; set; } = string.Empty;
    public string ProjectExternalId { get; set; } = string.Empty;
}

public sealed class MetaTombstoneRef
{
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
}
