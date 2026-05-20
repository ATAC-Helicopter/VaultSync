using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class MetadataStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _dbPath;
    private readonly bool _allowReadRecovery;

    public MetadataStore(string rootPath, bool allowReadRecovery = false)
    {
        string metaRoot = Path.Combine(rootPath, ".vaultsync", "meta");
        _dbPath = Path.Combine(metaRoot, "vaultsync.meta.db");
        _allowReadRecovery = allowReadRecovery;
    }

    public string DatabasePath => _dbPath;

    public void EnsureSchema()
    {
        using SqliteConnection c = Open(write: true);
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
              total_bytes INTEGER NOT NULL,
              diff_added INTEGER NOT NULL DEFAULT 0,
              diff_modified INTEGER NOT NULL DEFAULT 0,
              diff_deleted INTEGER NOT NULL DEFAULT 0,
              diff_net_bytes INTEGER NOT NULL DEFAULT 0,
              diff_top_paths_json TEXT NOT NULL DEFAULT '[]'
            );

            CREATE TABLE IF NOT EXISTS backups(
              external_id TEXT PRIMARY KEY,
              project_external_id TEXT NOT NULL,
              snapshot_external_id TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              type TEXT NOT NULL,
              backup_mode TEXT NOT NULL DEFAULT 'full',
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

        EnsureColumn(c, "backups", "origin_machine_name", "ALTER TABLE backups ADD COLUMN origin_machine_name TEXT NOT NULL DEFAULT '';");
        EnsureColumn(c, "backups", "is_protected", "ALTER TABLE backups ADD COLUMN is_protected INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(c, "backups", "enc_flag", "ALTER TABLE backups ADD COLUMN enc_flag INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(c, "backups", "kdf_params_json", "ALTER TABLE backups ADD COLUMN kdf_params_json TEXT NOT NULL DEFAULT '{}';");
        EnsureColumn(c, "backups", "backup_mode", "ALTER TABLE backups ADD COLUMN backup_mode TEXT NOT NULL DEFAULT 'full';");
        EnsureColumn(c, "snapshots", "diff_added", "ALTER TABLE snapshots ADD COLUMN diff_added INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(c, "snapshots", "diff_modified", "ALTER TABLE snapshots ADD COLUMN diff_modified INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(c, "snapshots", "diff_deleted", "ALTER TABLE snapshots ADD COLUMN diff_deleted INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(c, "snapshots", "diff_net_bytes", "ALTER TABLE snapshots ADD COLUMN diff_net_bytes INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(c, "snapshots", "diff_top_paths_json", "ALTER TABLE snapshots ADD COLUMN diff_top_paths_json TEXT NOT NULL DEFAULT '[]';");
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string alterSql)
    {
        if (HasColumn(connection, tableName, columnName))
        {
            return;
        }

        connection.Execute(alterSql);
    }

    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        string escapedTable = tableName.Replace("'", "''", StringComparison.Ordinal);
        IEnumerable<dynamic> columns = connection.Query($"PRAGMA table_info('{escapedTable}');");
        foreach (dynamic column in columns)
        {
            string? name = (string?)column.name;
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public MetaInfo? GetMetaInfo()
    {
        using SqliteConnection? c = TryOpenRead();
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
        using SqliteConnection c = Open(write: true);
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
        using SqliteConnection c = Open(write: true);
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
        using SqliteConnection c = Open(write: true);
        c.Execute(
            """
            INSERT INTO snapshots(
              external_id,
              project_external_id,
              created_utc,
              file_count,
              total_bytes,
              diff_added,
              diff_modified,
              diff_deleted,
              diff_net_bytes,
              diff_top_paths_json)
            VALUES(
              @ExternalId,
              @ProjectExternalId,
              @CreatedUtc,
              @FileCount,
              @TotalBytes,
              @DiffAdded,
              @DiffModified,
              @DiffDeleted,
              @DiffNetBytes,
              @DiffTopPathsJson)
            ON CONFLICT(external_id) DO UPDATE SET
              project_external_id = excluded.project_external_id,
              created_utc = excluded.created_utc,
              file_count = excluded.file_count,
              total_bytes = excluded.total_bytes,
              diff_added = excluded.diff_added,
              diff_modified = excluded.diff_modified,
              diff_deleted = excluded.diff_deleted,
              diff_net_bytes = excluded.diff_net_bytes,
              diff_top_paths_json = excluded.diff_top_paths_json;
            """,
            new
            {
                snapshot.ExternalId,
                snapshot.ProjectExternalId,
                CreatedUtc = ToUtcString(snapshot.CreatedUtc),
                snapshot.FileCount,
                snapshot.TotalBytes,
                snapshot.DiffAdded,
                snapshot.DiffModified,
                snapshot.DiffDeleted,
                snapshot.DiffNetBytes,
                snapshot.DiffTopPathsJson
            });
    }

    public void UpsertBackup(MetaBackup backup)
    {
        var descriptor = BackupCryptoDescriptor.FromMetadata(backup.IsEncrypted, backup.KdfParamsJson);
        string descriptorJson = descriptor.ToMetadataJson(backup.IsEncrypted);

        using SqliteConnection c = Open(write: true);
        c.Execute(
            """
            INSERT INTO backups(external_id, project_external_id, snapshot_external_id, created_utc, type, backup_mode, total_bytes, path_rel, destination_alias, origin_machine_name, is_protected, enc_flag, kdf_params_json)
            VALUES(@ExternalId, @ProjectExternalId, @SnapshotExternalId, @CreatedUtc, @Type, @BackupMode, @TotalBytes, @PathRel, @DestinationAlias, @OriginMachineName, @IsProtected, @EncFlag, @KdfParamsJson)
            ON CONFLICT(external_id) DO UPDATE SET
              project_external_id = excluded.project_external_id,
              snapshot_external_id = excluded.snapshot_external_id,
              created_utc = excluded.created_utc,
              type = excluded.type,
              backup_mode = excluded.backup_mode,
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
                BackupMode = BackupModes.Normalize(backup.BackupMode),
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
        using SqliteConnection c = Open(write: true);
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
        using SqliteConnection? c = TryOpenRead();
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
        using SqliteConnection? c = TryOpenRead();
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

        using SqliteConnection? c = TryOpenRead();
        return SafeExecuteScalarInt(
            c,
            "SELECT 1 FROM projects WHERE external_id = @id LIMIT 1;",
            new { id = externalId }) == 1;
    }

    public IEnumerable<MetaSnapshot> ListSnapshots()
    {
        using SqliteConnection? c = TryOpenRead();
        if (c is null)
            return Array.Empty<MetaSnapshot>();

        HashSet<string> snapshotColumns = GetTableColumns(c, "snapshots");
        string diffAddedProjection = snapshotColumns.Contains("diff_added")
            ? "diff_added as DiffAdded"
            : "0 as DiffAdded";
        string diffModifiedProjection = snapshotColumns.Contains("diff_modified")
            ? "diff_modified as DiffModified"
            : "0 as DiffModified";
        string diffDeletedProjection = snapshotColumns.Contains("diff_deleted")
            ? "diff_deleted as DiffDeleted"
            : "0 as DiffDeleted";
        string diffNetBytesProjection = snapshotColumns.Contains("diff_net_bytes")
            ? "diff_net_bytes as DiffNetBytes"
            : "0 as DiffNetBytes";
        string diffTopPathsProjection = snapshotColumns.Contains("diff_top_paths_json")
            ? "diff_top_paths_json as DiffTopPathsJson"
            : "'[]' as DiffTopPathsJson";

        string sql = $"""
            SELECT
              external_id as ExternalId,
              project_external_id as ProjectExternalId,
              created_utc as CreatedUtc,
              file_count as FileCount,
              total_bytes as TotalBytes,
              {diffAddedProjection},
              {diffModifiedProjection},
              {diffDeletedProjection},
              {diffNetBytesProjection},
              {diffTopPathsProjection}
            FROM snapshots;
            """;

        return SafeQuery<MetaSnapshot>(c, sql);
    }

    public IEnumerable<MetaSnapshotRef> ListSnapshotRefs()
    {
        using SqliteConnection? c = TryOpenRead();
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
        using SqliteConnection? c = TryOpenRead();
        if (c is null)
            return Array.Empty<MetaBackup>();

        HashSet<string> backupColumns = GetTableColumns(c, "backups");
        string originMachineProjection = backupColumns.Contains("origin_machine_name")
            ? "origin_machine_name as OriginMachineName"
            : "'' as OriginMachineName";
        string protectedProjection = backupColumns.Contains("is_protected")
            ? "is_protected as IsProtected"
            : "0 as IsProtected";
        string encryptedProjection = backupColumns.Contains("enc_flag")
            ? "enc_flag as IsEncrypted"
            : "0 as IsEncrypted";
        string descriptorProjection = backupColumns.Contains("kdf_params_json")
            ? "kdf_params_json as KdfParamsJson"
            : "'{}' as KdfParamsJson";
        string backupModeProjection = backupColumns.Contains("backup_mode")
            ? "backup_mode as BackupMode"
            : "'full' as BackupMode";

        string sql = $"""
            SELECT
              external_id as ExternalId,
              project_external_id as ProjectExternalId,
              snapshot_external_id as SnapshotExternalId,
              created_utc as CreatedUtc,
              type,
              {backupModeProjection},
              total_bytes as TotalBytes,
              path_rel as PathRel,
              destination_alias as DestinationAlias,
              {originMachineProjection},
              {protectedProjection},
              {encryptedProjection},
              {descriptorProjection}
            FROM backups;
            """;

        return SafeQuery<MetaBackup>(c, sql);
    }

    public IEnumerable<MetaBackupRef> ListBackupRefs()
    {
        using SqliteConnection? c = TryOpenRead();
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
        using SqliteConnection? c = TryOpenRead();
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
        using SqliteConnection? c = TryOpenRead();
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
        int attempts = write && IsLikelyNetworkPath(_dbPath) ? 3 : 1;
        int delayMs = 200;
        Exception? lastError = null;
        for (int attempt = 0; attempt < attempts; attempt++)
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
        string? dir = Path.GetDirectoryName(_dbPath);
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
            int timeoutMs = IsLikelyNetworkPath(_dbPath) ? 10000 : 5000;
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

            return Open(write: _allowReadRecovery);
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

    private static HashSet<string> GetTableColumns(SqliteConnection connection, string tableName)
    {
        try
        {
            return connection
                .Query<TableColumnInfo>($"PRAGMA table_info({tableName});")
                .Select(c => c.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        {
            try
            {
                string root = GetVolumeRoot(path);
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    var driveInfo = new DriveInfo(root);
                    return driveInfo.DriveType == DriveType.Network;
                }
            }
            catch
            {
                // Fall through to non-network default.
            }
        }

        if (path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/run/media/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (path.Contains("/Library/Application Support/VaultSync/mounts/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string GetVolumeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        string normalized = path.Replace('\\', '/').TrimEnd('/');
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], "Volumes", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return "/" + parts[0] + "/" + parts[1];
    }

    private static string ToUtcString(DateTime utc) =>
        utc.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);

    private sealed class TableColumnInfo
    {
        public string Name { get; set; } = string.Empty;
    }
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
    public int DiffAdded { get; set; }
    public int DiffModified { get; set; }
    public int DiffDeleted { get; set; }
    public long DiffNetBytes { get; set; }
    public string DiffTopPathsJson { get; set; } = "[]";
}

public sealed class MetaBackup
{
    public string ExternalId { get; set; } = string.Empty;
    public string ProjectExternalId { get; set; } = string.Empty;
    public string SnapshotExternalId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string Type { get; set; } = string.Empty;
    public string BackupMode { get; set; } = BackupModes.Full;
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
