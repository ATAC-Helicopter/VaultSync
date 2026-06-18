using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Models;

namespace VaultSync.Core.Repositories
{
    public class SqliteRepository(string dbPath)
    {
        private static readonly ConcurrentDictionary<string, byte> JournalModeConfigured = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _dbPath = dbPath;

        private SqliteConnection Open()
        {
            string? dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 10
            };

            var conn = new SqliteConnection(builder.ConnectionString);
            conn.Open();
            ConfigureConnection(conn, _dbPath);
            return conn;
        }

        private static void ConfigureConnection(SqliteConnection connection, string dbPath)
        {
            connection.Execute("PRAGMA foreign_keys = ON;");
            connection.Execute("PRAGMA busy_timeout = 10000;");
            connection.Execute("PRAGMA synchronous = NORMAL;");

            string journalModeKey = NormalizeDbPathKey(dbPath);
            if (!JournalModeConfigured.TryAdd(journalModeKey, 0))
                return;

            try
            {
                _ = connection.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");
            }
            catch (SqliteException)
            {
                // Some mounted or locked destinations cannot switch journal mode; busy_timeout still applies.
            }
            catch (IOException)
            {
                // Best-effort connection tuning must not block repository use.
            }
        }

        private static string NormalizeDbPathKey(string dbPath)
        {
            try
            {
                return Path.GetFullPath(dbPath);
            }
            catch
            {
                return dbPath;
            }
        }

        public (int Snapshots, int Files) DeleteSnapshotsById(string projectName, IEnumerable<int> snapshotIds)
        {
            if (string.IsNullOrWhiteSpace(projectName))
                throw new ArgumentException("Project name is required", nameof(projectName));

            ArgumentNullException.ThrowIfNull(snapshotIds);

            int[] ids = [.. snapshotIds.Distinct()];
            if (ids.Length == 0)
                return (0, 0);

            using SqliteConnection conn = Open();
            using SqliteTransaction tx = conn.BeginTransaction();

            int pid = conn.ExecuteScalar<int?>(
                "SELECT id FROM projects WHERE name = @name;",
                new { name = projectName },
                tx)
                ?? throw new InvalidOperationException($"Project '{projectName}' not found");

            int[] validIds = [.. conn.Query<int>(
                """
                SELECT id FROM snapshots
                WHERE project_id = @pid AND id IN @ids
                ORDER BY id;
                """,
                new { pid, ids },
                tx)];

            if (validIds.Length == 0)
            {
                tx.Commit();
                return (0, 0);
            }

            int filesDeleted = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM files WHERE snapshot_id IN @ids;",
                new { ids = validIds },
                tx);

            conn.Execute("DELETE FROM snapshots WHERE id IN @ids;", new { ids = validIds }, tx);

            tx.Commit();
            return (validIds.Length, filesDeleted);
        }

        public void EnsureSchema()
        {
            using SqliteConnection c = Open();

            CreateBaseSchema(c);
            ApplyMigrations(c);
            CreateIndexes(c);
            NormalizeBackupPathSeparators(c);
        }

        private static void CreateBaseSchema(SqliteConnection connection)
        {
            connection.Execute("""
        -- Projects
        CREATE TABLE IF NOT EXISTS projects(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          external_id TEXT NOT NULL DEFAULT '',
          needs_restore INTEGER NOT NULL DEFAULT 0,
          preferred_destination_id TEXT,
          encryption_policy TEXT NOT NULL DEFAULT 'inherit',
          encryption_key_ref TEXT,
          restore_mode TEXT NOT NULL DEFAULT 'direct',
          verification_policy TEXT NOT NULL DEFAULT 'always',
          tags TEXT NOT NULL DEFAULT '',
          name TEXT NOT NULL UNIQUE,
          root_path TEXT NOT NULL,
          preset TEXT NOT NULL,
          created_utc TEXT NOT NULL
        );

        -- Snapshots (cascade to files when a snapshot is deleted; cascade to snapshots when project is deleted)
        CREATE TABLE IF NOT EXISTS snapshots(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          external_id TEXT NOT NULL DEFAULT '',
          project_id INTEGER NOT NULL,
          created_utc TEXT NOT NULL,
          file_count INTEGER NOT NULL,
          total_bytes INTEGER NOT NULL,
          diff_added INTEGER NOT NULL DEFAULT 0,
          diff_modified INTEGER NOT NULL DEFAULT 0,
          diff_deleted INTEGER NOT NULL DEFAULT 0,
          diff_net_bytes INTEGER NOT NULL DEFAULT 0,
          diff_top_paths_json TEXT NOT NULL DEFAULT '[]',
          FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE
        );

        -- Files (cascade when snapshot deleted)
        CREATE TABLE IF NOT EXISTS files(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          snapshot_id INTEGER NOT NULL,
          rel_path TEXT NOT NULL,
          size INTEGER NOT NULL,
          mtime_utc TEXT NOT NULL,
          hash_sha256 TEXT NOT NULL,
          FOREIGN KEY(snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE
        );

        -- Backups
        CREATE TABLE IF NOT EXISTS backups(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          external_id TEXT NOT NULL DEFAULT '',
          project_id INTEGER NOT NULL,
          snapshot_id INTEGER NOT NULL,
          created_utc TEXT NOT NULL,
          type TEXT NOT NULL,
          backup_mode TEXT NOT NULL DEFAULT 'full',
          total_bytes INTEGER NOT NULL,
          path TEXT NOT NULL,
          destination_path TEXT NOT NULL DEFAULT '',
          destination_alias TEXT NOT NULL DEFAULT '',
          origin_machine_name TEXT NOT NULL DEFAULT '',
          is_encrypted INTEGER NOT NULL DEFAULT 0,
          crypto_descriptor_json TEXT NOT NULL DEFAULT '{}',
          is_imported INTEGER NOT NULL DEFAULT 0,
          FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE,
          FOREIGN KEY(snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE
        );

        -- 1.8 history metadata: user-facing labels for important snapshots.
        CREATE TABLE IF NOT EXISTS snapshot_history_metadata(
          snapshot_id INTEGER PRIMARY KEY,
          label TEXT NOT NULL DEFAULT '',
          note TEXT NOT NULL DEFAULT '',
          tags TEXT NOT NULL DEFAULT '',
          is_protected INTEGER NOT NULL DEFAULT 0,
          is_known_good INTEGER NOT NULL DEFAULT 0,
          created_utc TEXT NOT NULL,
          updated_utc TEXT NOT NULL,
          FOREIGN KEY(snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE
        );

        -- 1.8 restore events: restore operations become first-class history nodes.
        CREATE TABLE IF NOT EXISTS restore_history_events(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          project_id INTEGER NOT NULL,
          backup_id INTEGER NOT NULL,
          snapshot_id INTEGER NOT NULL,
          created_utc TEXT NOT NULL,
          restore_mode TEXT NOT NULL DEFAULT 'direct',
          target_path TEXT NOT NULL DEFAULT '',
          status TEXT NOT NULL DEFAULT 'completed',
          note TEXT NOT NULL DEFAULT '',
          FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE,
          FOREIGN KEY(backup_id) REFERENCES backups(id) ON DELETE CASCADE,
          FOREIGN KEY(snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE
        );
    """);
        }

        private static void ApplyMigrations(SqliteConnection connection)
        {
            EnsureColumnExists(connection, "backups", "is_protected", "ALTER TABLE backups ADD COLUMN is_protected INTEGER NOT NULL DEFAULT 0;");
            EnsureColumnExists(connection, "backups", "is_imported", "ALTER TABLE backups ADD COLUMN is_imported INTEGER NOT NULL DEFAULT 0;");
            EnsureColumnExists(connection, "backups", "destination_path", "ALTER TABLE backups ADD COLUMN destination_path TEXT NOT NULL DEFAULT '';");
            EnsureColumnExists(connection, "backups", "destination_alias", "ALTER TABLE backups ADD COLUMN destination_alias TEXT NOT NULL DEFAULT '';");
            EnsureColumnExists(connection, "backups", "origin_machine_name", "ALTER TABLE backups ADD COLUMN origin_machine_name TEXT NOT NULL DEFAULT '';");
            EnsureColumnExists(connection, "backups", "is_encrypted", "ALTER TABLE backups ADD COLUMN is_encrypted INTEGER NOT NULL DEFAULT 0;");
            EnsureColumnExists(connection, "backups", "crypto_descriptor_json", "ALTER TABLE backups ADD COLUMN crypto_descriptor_json TEXT NOT NULL DEFAULT '{}';");
            EnsureColumnExists(connection, "backups", "backup_mode", "ALTER TABLE backups ADD COLUMN backup_mode TEXT NOT NULL DEFAULT 'full';");
            EnsureColumnExists(connection, "projects", "external_id", "ALTER TABLE projects ADD COLUMN external_id TEXT NOT NULL DEFAULT '';");
            EnsureColumnExists(connection, "projects", "needs_restore", "ALTER TABLE projects ADD COLUMN needs_restore INTEGER NOT NULL DEFAULT 0;");
            EnsureColumnExists(connection, "projects", "preferred_destination_id", "ALTER TABLE projects ADD COLUMN preferred_destination_id TEXT;");
            EnsureColumnExists(connection, "projects", "encryption_policy", "ALTER TABLE projects ADD COLUMN encryption_policy TEXT NOT NULL DEFAULT 'inherit';");
            EnsureColumnExists(connection, "projects", "encryption_key_ref", "ALTER TABLE projects ADD COLUMN encryption_key_ref TEXT;");
            EnsureColumnExists(connection, "projects", "restore_mode", "ALTER TABLE projects ADD COLUMN restore_mode TEXT NOT NULL DEFAULT 'direct';");
            EnsureColumnExists(connection, "projects", "verification_policy", "ALTER TABLE projects ADD COLUMN verification_policy TEXT NOT NULL DEFAULT 'always';");
            EnsureColumnExists(connection, "projects", "tags", "ALTER TABLE projects ADD COLUMN tags TEXT NOT NULL DEFAULT '';");
            EnsureColumnExists(connection, "snapshots", "external_id", "ALTER TABLE snapshots ADD COLUMN external_id TEXT NOT NULL DEFAULT '';");
            EnsureColumnExists(connection, "snapshots", "diff_added", "ALTER TABLE snapshots ADD COLUMN diff_added INTEGER NOT NULL DEFAULT 0;");
            EnsureColumnExists(connection, "snapshots", "diff_modified", "ALTER TABLE snapshots ADD COLUMN diff_modified INTEGER NOT NULL DEFAULT 0;");
            EnsureColumnExists(connection, "snapshots", "diff_deleted", "ALTER TABLE snapshots ADD COLUMN diff_deleted INTEGER NOT NULL DEFAULT 0;");
            EnsureColumnExists(connection, "snapshots", "diff_net_bytes", "ALTER TABLE snapshots ADD COLUMN diff_net_bytes INTEGER NOT NULL DEFAULT 0;");
            EnsureColumnExists(connection, "snapshots", "diff_top_paths_json", "ALTER TABLE snapshots ADD COLUMN diff_top_paths_json TEXT NOT NULL DEFAULT '[]';");
            EnsureColumnExists(connection, "backups", "external_id", "ALTER TABLE backups ADD COLUMN external_id TEXT NOT NULL DEFAULT '';");
        }

        private static void CreateIndexes(SqliteConnection connection)
        {
            connection.Execute("""
        CREATE INDEX IF NOT EXISTS idx_projects_name ON projects(name);
        CREATE INDEX IF NOT EXISTS idx_projects_external ON projects(external_id);

        CREATE INDEX IF NOT EXISTS idx_snapshots_project_created
          ON snapshots(project_id, created_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_snapshots_external ON snapshots(external_id);

        CREATE INDEX IF NOT EXISTS idx_files_snapshot ON files(snapshot_id);

        CREATE INDEX IF NOT EXISTS idx_backups_project_created
          ON backups(project_id, created_utc DESC);

        CREATE INDEX IF NOT EXISTS idx_backups_created
          ON backups(created_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_backups_external ON backups(external_id);

        CREATE INDEX IF NOT EXISTS idx_snapshot_history_known_good
          ON snapshot_history_metadata(is_known_good);
        CREATE INDEX IF NOT EXISTS idx_snapshot_history_protected
          ON snapshot_history_metadata(is_protected);

        CREATE INDEX IF NOT EXISTS idx_restore_history_project_created
          ON restore_history_events(project_id, created_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_restore_history_snapshot
          ON restore_history_events(snapshot_id);

        -- Avoid duplicate file rows per snapshot (same logical path)
        CREATE UNIQUE INDEX IF NOT EXISTS ux_files_snapshot_rel
          ON files(snapshot_id, rel_path);
    """);
        }

        [SuppressMessage("Major Code Smell", "S1144:Unused private types or members", Justification = "Dapper populates this private row type by reflection.")]
        [SuppressMessage("Major Code Smell", "S3459:Unassigned members should be removed", Justification = "Dapper populates this private row type by reflection.")]
        private sealed class BackupPathRow
        {
            public long Id { get; init; }
            public string Path { get; init; } = string.Empty;
        }

        private static void NormalizeBackupPathSeparators(SqliteConnection connection)
        {
            var rows = connection.Query<BackupPathRow>(
                "SELECT id, path FROM backups WHERE path LIKE '%\\\\%' OR path LIKE '%/%';").ToList();
            if (rows.Count == 0)
                return;

            char separator = Path.DirectorySeparatorChar;
            foreach (BackupPathRow? row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Path))
                    continue;

                string normalized = row.Path
                    .Replace('\\', separator)
                    .Replace('/', separator)
                    .TrimStart(separator);

                if (string.Equals(normalized, row.Path, StringComparison.Ordinal))
                    continue;

                connection.Execute(
                    "UPDATE backups SET path = @path WHERE id = @id;",
                    new { path = normalized, id = row.Id });
            }
        }

        /// <summary>
        /// Development helper: clears all data from the database tables without
        /// deleting the database file or any original project files on disk.
        /// After this call, the DB is effectively in a "fresh" state but with the same schema.
        /// </summary>
        public void ResetAllData()
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction tx = connection.BeginTransaction();

            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
DELETE FROM backups;
DELETE FROM files;
DELETE FROM snapshots;
DELETE FROM projects;
DELETE FROM sqlite_sequence;";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        // ---------- Projects ----------
        public Project? GetProjectByName(string name)
        {
            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<Project>(
                "SELECT id, external_id as ExternalId, needs_restore as NeedsRestore, preferred_destination_id as PreferredDestinationId, encryption_policy as EncryptionPolicy, encryption_key_ref as EncryptionKeyRef, restore_mode as RestoreMode, verification_policy as VerificationPolicy, tags as Tags, name, root_path as RootPath, preset as Preset, created_utc as CreatedUtc FROM projects WHERE name=@name",
                new { name });
        }

        public Project? GetProjectById(int id)
        {
            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<Project>(
                "SELECT id, external_id as ExternalId, needs_restore as NeedsRestore, preferred_destination_id as PreferredDestinationId, encryption_policy as EncryptionPolicy, encryption_key_ref as EncryptionKeyRef, restore_mode as RestoreMode, verification_policy as VerificationPolicy, tags as Tags, name, root_path as RootPath, preset as Preset, created_utc as CreatedUtc FROM projects WHERE id=@id",
                new { id });
        }

        public IReadOnlyDictionary<string, int> GetProjectExternalIdMap()
        {
            using SqliteConnection c = Open();
            IEnumerable<(int Id, string ExternalId)> rows = c.Query<(int Id, string ExternalId)>(
                "SELECT id as Id, external_id as ExternalId FROM projects WHERE external_id != '';");

            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ExternalId))
                .ToDictionary(row => row.ExternalId, row => row.Id, StringComparer.OrdinalIgnoreCase);
        }

        public void RemoveProject(int projectId)
        {
            using SqliteConnection c = Open();

            // Because foreign_keys are ON and snapshots/files reference projects
            // with ON DELETE CASCADE, deleting the project row will also delete
            // its snapshots and files.
            const string sql = "DELETE FROM projects WHERE id = @id;";
            c.Execute(sql, new { id = projectId });
        }

        public IEnumerable<Project> ListProjects()
        {
            using SqliteConnection c = Open();
            return c.Query<Project>(
                "SELECT id, external_id as ExternalId, needs_restore as NeedsRestore, preferred_destination_id as PreferredDestinationId, encryption_policy as EncryptionPolicy, encryption_key_ref as EncryptionKeyRef, restore_mode as RestoreMode, verification_policy as VerificationPolicy, tags as Tags, name, root_path as RootPath, preset as Preset, created_utc as CreatedUtc FROM projects ORDER BY name");
        }

        /// <summary>
        /// Returns all projects in the database. This is a convenience wrapper used by the UI dashboard.
        /// </summary>
        public IEnumerable<Project> GetAllProjects()
        {
            // Delegate to the existing ListProjects implementation to keep one query definition.
            return ListProjects();
        }

        /// <summary>
        /// Async helper for retrieving all projects without blocking the caller thread.
        /// Intended for UI code; uses true async DB access.
        /// </summary>
        public async Task<List<Project>> GetAllProjectsAsync(CancellationToken ct = default)
        {
            const string sql =
                "SELECT id, external_id as ExternalId, needs_restore as NeedsRestore, preferred_destination_id as PreferredDestinationId, encryption_policy as EncryptionPolicy, encryption_key_ref as EncryptionKeyRef, restore_mode as RestoreMode, verification_policy as VerificationPolicy, tags as Tags, name, root_path as RootPath, preset as Preset, created_utc as CreatedUtc FROM projects ORDER BY name";
            await using SqliteConnection c = Open();
            IEnumerable<Project> rows = await c.QueryAsync<Project>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
            return [.. rows];
        }

        public int AddProject(Project p)
        {
            using SqliteConnection c = Open();
            string externalId = string.IsNullOrWhiteSpace(p.ExternalId)
                ? NewExternalId()
                : p.ExternalId;
            return c.ExecuteScalar<int>(
                """
                INSERT INTO projects(external_id, needs_restore, preferred_destination_id, encryption_policy, encryption_key_ref, restore_mode, verification_policy, tags, name, root_path, preset, created_utc)
                VALUES(@ExternalId, @NeedsRestore, @PreferredDestinationId, @EncryptionPolicy, @EncryptionKeyRef, @RestoreMode, @VerificationPolicy, @Tags, @Name, @RootPath, @Preset, @CreatedUtc);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ExternalId = externalId,
                    NeedsRestore = p.NeedsRestore ? 1 : 0,
                    PreferredDestinationId = string.IsNullOrWhiteSpace(p.PreferredDestinationId) ? null : p.PreferredDestinationId,
                    EncryptionPolicy = ProjectEncryptionPolicy.Normalize(p.EncryptionPolicy),
                    EncryptionKeyRef = string.IsNullOrWhiteSpace(p.EncryptionKeyRef) ? null : p.EncryptionKeyRef,
                    RestoreMode = ProjectRestoreMode.Normalize(p.RestoreMode),
                    VerificationPolicy = ProjectVerificationPolicy.Normalize(p.VerificationPolicy),
                    Tags = string.IsNullOrWhiteSpace(p.Tags) ? string.Empty : p.Tags,
                    p.Name,
                    p.RootPath,
                    p.Preset,
                    CreatedUtc = p.CreatedUtc.ToString("u", CultureInfo.InvariantCulture)
                });
        }

        public void UpdateProjectNeedsRestore(int projectId, bool needsRestore)
        {
            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE projects SET needs_restore = @needs WHERE id = @id;",
                new { needs = needsRestore ? 1 : 0, id = projectId });
        }

        public void UpdateProjectPreferredDestination(int projectId, string? preferredDestinationId)
        {
            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE projects SET preferred_destination_id = @preferred WHERE id = @id;",
                new
                {
                    preferred = string.IsNullOrWhiteSpace(preferredDestinationId) ? null : preferredDestinationId,
                    id = projectId
                });
        }

        public void UpdateProjectPreset(int projectId, string? preset)
        {
            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE projects SET preset = @preset WHERE id = @id;",
                new
                {
                    preset = string.IsNullOrWhiteSpace(preset) ? string.Empty : preset.Trim(),
                    id = projectId
                });
        }

        public void UpdateProjectEncryptionPolicy(int projectId, string? encryptionPolicy)
        {
            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE projects SET encryption_policy = @policy WHERE id = @id;",
                new
                {
                    policy = ProjectEncryptionPolicy.Normalize(encryptionPolicy),
                    id = projectId
                });
        }

        public void UpdateProjectEncryptionSettings(int projectId, string? encryptionPolicy, string? encryptionKeyRef)
        {
            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE projects SET encryption_policy = @policy, encryption_key_ref = @keyRef WHERE id = @id;",
                new
                {
                    policy = ProjectEncryptionPolicy.Normalize(encryptionPolicy),
                    keyRef = string.IsNullOrWhiteSpace(encryptionKeyRef) ? null : encryptionKeyRef,
                    id = projectId
                });
        }

        public void UpdateProjectRestoreMode(int projectId, string? restoreMode)
        {
            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE projects SET restore_mode = @mode WHERE id = @id;",
                new
                {
                    mode = ProjectRestoreMode.Normalize(restoreMode),
                    id = projectId
                });
        }

        public void UpdateProjectVerificationPolicy(int projectId, string? verificationPolicy)
        {
            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE projects SET verification_policy = @policy WHERE id = @id;",
                new
                {
                    policy = ProjectVerificationPolicy.Normalize(verificationPolicy),
                    id = projectId
                });
        }

        public void UpdateProjectTags(int projectId, string? tags)
        {
            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE projects SET tags = @tags WHERE id = @id;",
                new
                {
                    tags = string.IsNullOrWhiteSpace(tags) ? string.Empty : tags.Trim(),
                    id = projectId
                });
        }

        public Project? GetProjectByExternalId(string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return null;

            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<Project>(
                """
                SELECT id, external_id as ExternalId, preferred_destination_id as PreferredDestinationId, encryption_policy as EncryptionPolicy, encryption_key_ref as EncryptionKeyRef, restore_mode as RestoreMode, verification_policy as VerificationPolicy, tags as Tags, name, root_path as RootPath, preset as Preset, created_utc as CreatedUtc
                FROM projects
                WHERE external_id = @externalId
                LIMIT 1;
                """,
                new { externalId });
        }

        public void UpdateProjectExternalId(int projectId, string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return;

            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE projects SET external_id = @externalId WHERE id = @id;",
                new { externalId, id = projectId });
        }

        public bool UpdateProjectPath(string name, string newPath, out string? oldPath)
        {
            using SqliteConnection c = Open();
            Project? p = GetProjectByName(name);
            if (p is null) { oldPath = null; return false; }

            oldPath = p.RootPath;
            int rows = c.Execute(
                "UPDATE projects SET root_path=@newPath WHERE id=@id",
                new { newPath, id = p.Id });
            return rows > 0;
        }

        public bool UpdateProjectPath(int projectId, string newPath, out string? oldPath)
        {
            using SqliteConnection c = Open();
            Project? p = GetProjectById(projectId);
            if (p is null)
            {
                oldPath = null;
                return false;
            }

            oldPath = p.RootPath;
            int rows = c.Execute(
                "UPDATE projects SET root_path=@newPath WHERE id=@id",
                new { newPath, id = projectId });
            return rows > 0;
        }

        public DeleteStats DeleteProjectCascade(string name)
        {
            using SqliteConnection c = Open();
            using SqliteTransaction tx = c.BeginTransaction();

            int projId = c.ExecuteScalar<int>("SELECT id FROM projects WHERE name=@name", new { name }, tx);
            if (projId == 0)
                return new DeleteStats(0, 0, 0);

            var snaps = c.Query<int>("SELECT id FROM snapshots WHERE project_id=@pid", new { pid = projId }, tx).ToList();
            int filesCount = 0;
            if (snaps.Count > 0)
            {
                filesCount = c.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM files WHERE snapshot_id IN @sids",
                    new { sids = snaps }, tx);
            }

            // Delete snapshots; files will be removed automatically via ON DELETE CASCADE
            int snapsDeleted = c.Execute("DELETE FROM snapshots WHERE project_id=@pid", new { pid = projId }, tx);
            int projDeleted = c.Execute("DELETE FROM projects WHERE id=@pid", new { pid = projId }, tx);

            tx.Commit();
            return new DeleteStats(projDeleted, snapsDeleted, filesCount);
        }

        // ---------- Snapshots ----------
        public int CreateSnapshot(int projectId, long fileCount, long totalBytes, SnapshotDiffSummary? diffSummary = null)
        {
            using SqliteConnection c = Open();
            string created = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);
            string externalId = NewExternalId();
            SnapshotDiffSummary summary = diffSummary ?? SnapshotDiffSummary.Empty;
            return c.ExecuteScalar<int>(
                """
                INSERT INTO snapshots(
                  external_id,
                  project_id,
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
                  @ProjectId,
                  @CreatedUtc,
                  @FileCount,
                  @TotalBytes,
                  @DiffAdded,
                  @DiffModified,
                  @DiffDeleted,
                  @DiffNetBytes,
                  @DiffTopPathsJson);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ExternalId = externalId,
                    ProjectId = projectId,
                    CreatedUtc = created,
                    FileCount = fileCount,
                    TotalBytes = totalBytes,
                    DiffAdded = summary.Added,
                    DiffModified = summary.Modified,
                    DiffDeleted = summary.Deleted,
                    DiffNetBytes = summary.NetSizeBytes,
                    DiffTopPathsJson = summary.TopChangedPathsJson
                });
        }

        public int CreateSnapshotFromMetadata(
            string externalId,
            int projectId,
            DateTime createdUtc,
            long fileCount,
            long totalBytes,
            SnapshotDiffSummary? diffSummary = null)
        {
            using SqliteConnection c = Open();
            string created = createdUtc.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
            string idToUse = string.IsNullOrWhiteSpace(externalId) ? NewExternalId() : externalId;
            SnapshotDiffSummary summary = diffSummary ?? SnapshotDiffSummary.Empty;
            return c.ExecuteScalar<int>(
                """
                INSERT INTO snapshots(
                  external_id,
                  project_id,
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
                  @ProjectId,
                  @CreatedUtc,
                  @FileCount,
                  @TotalBytes,
                  @DiffAdded,
                  @DiffModified,
                  @DiffDeleted,
                  @DiffNetBytes,
                  @DiffTopPathsJson);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ExternalId = idToUse,
                    ProjectId = projectId,
                    CreatedUtc = created,
                    FileCount = fileCount,
                    TotalBytes = totalBytes,
                    DiffAdded = summary.Added,
                    DiffModified = summary.Modified,
                    DiffDeleted = summary.Deleted,
                    DiffNetBytes = summary.NetSizeBytes,
                    DiffTopPathsJson = summary.TopChangedPathsJson
                });
        }

        public Snapshot? GetSnapshotByExternalId(string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return null;

            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<Snapshot>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  AS ProjectId,
                  created_utc AS CreatedUtc,
                  file_count  AS FileCount,
                  total_bytes AS TotalBytes,
                  diff_added AS DiffAdded,
                  diff_modified AS DiffModified,
                  diff_deleted AS DiffDeleted,
                  diff_net_bytes AS DiffNetBytes,
                  diff_top_paths_json AS DiffTopPathsJson
                FROM snapshots
                WHERE external_id = @externalId
                LIMIT 1;
                """,
                new { externalId });
        }

        public Snapshot? GetSnapshotById(int id)
        {
            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<Snapshot>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  AS ProjectId,
                  created_utc AS CreatedUtc,
                  file_count  AS FileCount,
                  total_bytes AS TotalBytes,
                  diff_added AS DiffAdded,
                  diff_modified AS DiffModified,
                  diff_deleted AS DiffDeleted,
                  diff_net_bytes AS DiffNetBytes,
                  diff_top_paths_json AS DiffTopPathsJson
                FROM snapshots
                WHERE id = @id
                LIMIT 1;
                """,
                new { id });
        }

        public IReadOnlyDictionary<string, int> GetSnapshotExternalIdMap()
        {
            using SqliteConnection c = Open();
            IEnumerable<(int Id, string ExternalId)> rows = c.Query<(int Id, string ExternalId)>(
                "SELECT id as Id, external_id as ExternalId FROM snapshots WHERE external_id != '';");

            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ExternalId))
                .ToDictionary(row => row.ExternalId, row => row.Id, StringComparer.OrdinalIgnoreCase);
        }

        public void UpdateSnapshotExternalId(int snapshotId, string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return;

            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE snapshots SET external_id = @externalId WHERE id = @id;",
                new { externalId, id = snapshotId });
        }

        public void UpdateSnapshotTotalBytes(int snapshotId, long totalBytes)
        {
            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE snapshots SET total_bytes = @totalBytes WHERE id = @id;",
                new { totalBytes = Math.Max(0, totalBytes), id = snapshotId });
        }

        public SnapshotHistoryMetadata? GetSnapshotHistoryMetadata(int snapshotId)
        {
            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<SnapshotHistoryMetadata>(
                """
                SELECT
                  snapshot_id as SnapshotId,
                  label as Label,
                  note as Note,
                  tags as Tags,
                  is_protected as IsProtected,
                  is_known_good as IsKnownGood,
                  created_utc as CreatedUtc,
                  updated_utc as UpdatedUtc
                FROM snapshot_history_metadata
                WHERE snapshot_id = @snapshotId
                LIMIT 1;
                """,
                new { snapshotId });
        }

        public IReadOnlyDictionary<int, SnapshotHistoryMetadata> GetSnapshotHistoryMetadataBySnapshotIds(IEnumerable<int> snapshotIds)
        {
            ArgumentNullException.ThrowIfNull(snapshotIds);

            int[] ids = [.. snapshotIds.Where(id => id > 0).Distinct()];
            if (ids.Length == 0)
                return new Dictionary<int, SnapshotHistoryMetadata>();

            using SqliteConnection c = Open();
            IEnumerable<SnapshotHistoryMetadata> rows = c.Query<SnapshotHistoryMetadata>(
                """
                SELECT
                  snapshot_id as SnapshotId,
                  label as Label,
                  note as Note,
                  tags as Tags,
                  is_protected as IsProtected,
                  is_known_good as IsKnownGood,
                  created_utc as CreatedUtc,
                  updated_utc as UpdatedUtc
                FROM snapshot_history_metadata
                WHERE snapshot_id IN @ids;
                """,
                new { ids });

            return rows.ToDictionary(row => row.SnapshotId);
        }

        public void UpsertSnapshotHistoryMetadata(SnapshotHistoryMetadata metadata)
        {
            if (metadata.SnapshotId <= 0)
                throw new ArgumentOutOfRangeException(nameof(metadata), "Snapshot id must be positive.");

            using SqliteConnection c = Open();
            using SqliteTransaction tx = c.BeginTransaction();

            int? projectId = c.QueryFirstOrDefault<int?>(
                "SELECT project_id FROM snapshots WHERE id = @snapshotId;",
                new { snapshotId = metadata.SnapshotId },
                tx);
            if (!projectId.HasValue)
                throw new InvalidOperationException($"Snapshot {metadata.SnapshotId} does not exist.");

            string now = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);
            if (metadata.IsKnownGood)
            {
                c.Execute(
                    """
                    UPDATE snapshot_history_metadata
                    SET is_known_good = 0,
                        updated_utc = @now
                    WHERE snapshot_id IN (
                        SELECT id FROM snapshots WHERE project_id = @projectId
                    );
                    """,
                    new { now, projectId = projectId.Value },
                    tx);
            }

            c.Execute(
                """
                INSERT INTO snapshot_history_metadata(
                  snapshot_id,
                  label,
                  note,
                  tags,
                  is_protected,
                  is_known_good,
                  created_utc,
                  updated_utc)
                VALUES(
                  @SnapshotId,
                  @Label,
                  @Note,
                  @Tags,
                  @IsProtected,
                  @IsKnownGood,
                  @CreatedUtc,
                  @UpdatedUtc)
                ON CONFLICT(snapshot_id) DO UPDATE SET
                  label = excluded.label,
                  note = excluded.note,
                  tags = excluded.tags,
                  is_protected = excluded.is_protected,
                  is_known_good = excluded.is_known_good,
                  updated_utc = excluded.updated_utc;
                """,
                new
                {
                    metadata.SnapshotId,
                    Label = metadata.Label ?? string.Empty,
                    Note = metadata.Note ?? string.Empty,
                    Tags = metadata.Tags ?? string.Empty,
                    IsProtected = metadata.IsProtected ? 1 : 0,
                    IsKnownGood = metadata.IsKnownGood ? 1 : 0,
                    CreatedUtc = (metadata.CreatedUtc == default ? DateTime.UtcNow : metadata.CreatedUtc)
                        .ToUniversalTime()
                        .ToString("u", CultureInfo.InvariantCulture),
                    UpdatedUtc = now
                },
                tx);

            c.Execute(
                "UPDATE backups SET is_protected = @isProtected WHERE snapshot_id = @snapshotId;",
                new
                {
                    snapshotId = metadata.SnapshotId,
                    isProtected = metadata.IsProtected ? 1 : 0
                },
                tx);

            tx.Commit();
        }

        public void DeleteSnapshotHistoryMetadata(int snapshotId)
        {
            using SqliteConnection c = Open();
            c.Execute("DELETE FROM snapshot_history_metadata WHERE snapshot_id = @snapshotId;", new { snapshotId });
        }

        public Snapshot? GetLatestSnapshot(int projectId)
        {
            return GetLatestSnapshotForProject(projectId);
        }

        public Snapshot? GetLatestLocalSnapshotForProject(int projectId)
        {
            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<Snapshot>(
                """
                SELECT
                  s.id,
                  s.external_id as ExternalId,
                  s.project_id  AS ProjectId,
                  s.created_utc AS CreatedUtc,
                  s.file_count  AS FileCount,
                  s.total_bytes AS TotalBytes,
                  s.diff_added AS DiffAdded,
                  s.diff_modified AS DiffModified,
                  s.diff_deleted AS DiffDeleted,
                  s.diff_net_bytes AS DiffNetBytes,
                  s.diff_top_paths_json AS DiffTopPathsJson
                FROM snapshots s
                WHERE s.project_id = @pid
                  AND NOT EXISTS (
                    SELECT 1
                    FROM backups b
                    WHERE b.snapshot_id = s.id
                      AND b.is_imported != 0
                  )
                ORDER BY s.created_utc DESC, s.id DESC
                LIMIT 1;
                """,
                new { pid = projectId });
        }

        public Snapshot? GetLatestSnapshotForProject(int projectId)
        {
            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<Snapshot>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  AS ProjectId,
                  created_utc AS CreatedUtc,
                  file_count  AS FileCount,
                  total_bytes AS TotalBytes,
                  diff_added AS DiffAdded,
                  diff_modified AS DiffModified,
                  diff_deleted AS DiffDeleted,
                  diff_net_bytes AS DiffNetBytes,
                  diff_top_paths_json AS DiffTopPathsJson
                FROM snapshots
                WHERE project_id = @pid
                ORDER BY created_utc DESC, id DESC
                LIMIT 1;
                """,
                new { pid = projectId });
        }

        public IReadOnlyDictionary<int, (DateTime CreatedUtc, long TotalBytes)> GetLatestSnapshotInfoByProject()
        {
            using SqliteConnection c = Open();
            IEnumerable<(int ProjectId, string CreatedUtc, long TotalBytes)> rows = c.Query<(int ProjectId, string CreatedUtc, long TotalBytes)>(
                """
                SELECT
                  project_id as ProjectId,
                  created_utc as CreatedUtc,
                  total_bytes as TotalBytes
                FROM (
                  SELECT
                    project_id,
                    created_utc,
                    total_bytes,
                    ROW_NUMBER() OVER (PARTITION BY project_id ORDER BY created_utc DESC, id DESC) as rn
                  FROM snapshots
                )
                WHERE rn = 1;
                """);

            const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            var map = new Dictionary<int, (DateTime CreatedUtc, long TotalBytes)>();
            foreach ((int projectId, string createdUtc, long totalBytes) in rows)
            {
                if (!DateTime.TryParse(createdUtc, CultureInfo.InvariantCulture, styles, out DateTime created))
                    created = DateTime.SpecifyKind(DateTime.Parse(createdUtc), DateTimeKind.Utc);

                map[projectId] = (created, totalBytes);
            }

            return map;
        }

        /// <summary>
        /// Returns all snapshots across all projects, newest first. Used by the UI dashboard.
        /// </summary>
        public IEnumerable<Snapshot> GetAllSnapshots()
        {
            using SqliteConnection c = Open();
            return c.Query<Snapshot>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id as ProjectId,
                  created_utc as CreatedUtc,
                  file_count as FileCount,
                  total_bytes as TotalBytes,
                  diff_added as DiffAdded,
                  diff_modified as DiffModified,
                  diff_deleted as DiffDeleted,
                  diff_net_bytes as DiffNetBytes,
                  diff_top_paths_json as DiffTopPathsJson
                FROM snapshots
                ORDER BY created_utc DESC
                """);
        }

        public IEnumerable<Snapshot> GetSnapshotsByIds(IEnumerable<int> snapshotIds)
        {
            ArgumentNullException.ThrowIfNull(snapshotIds);

            int[] ids = [.. snapshotIds
                .Where(id => id > 0)
                .Distinct()];
            if (ids.Length == 0)
                return [];

            using SqliteConnection c = Open();
            return c.Query<Snapshot>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id as ProjectId,
                  created_utc as CreatedUtc,
                  file_count as FileCount,
                  total_bytes as TotalBytes,
                  diff_added as DiffAdded,
                  diff_modified as DiffModified,
                  diff_deleted as DiffDeleted,
                  diff_net_bytes as DiffNetBytes,
                  diff_top_paths_json as DiffTopPathsJson
                FROM snapshots
                WHERE id IN @ids
                """,
                new { ids });
        }

        /// <summary>
        /// Async helper for retrieving all snapshots without blocking the UI thread.
        /// Uses true async DB access.
        /// </summary>
        public async Task<List<Snapshot>> GetAllSnapshotsAsync(CancellationToken ct = default)
        {
            const string sql =
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id as ProjectId,
                  created_utc as CreatedUtc,
                  file_count as FileCount,
                  total_bytes as TotalBytes,
                  diff_added as DiffAdded,
                  diff_modified as DiffModified,
                  diff_deleted as DiffDeleted,
                  diff_net_bytes as DiffNetBytes,
                  diff_top_paths_json as DiffTopPathsJson
                FROM snapshots
                ORDER BY created_utc DESC
                """;
            await using SqliteConnection c = Open();
            IEnumerable<Snapshot> rows = await c.QueryAsync<Snapshot>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
            return [.. rows];
        }

        public IEnumerable<Snapshot> GetSnapshotsForProject(string projectName)
        {
            using SqliteConnection c = Open();
            return c.Query<Snapshot>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id as ProjectId,
                  created_utc as CreatedUtc,
                  file_count as FileCount,
                  total_bytes as TotalBytes,
                  diff_added as DiffAdded,
                  diff_modified as DiffModified,
                  diff_deleted as DiffDeleted,
                  diff_net_bytes as DiffNetBytes,
                  diff_top_paths_json as DiffTopPathsJson
                FROM snapshots
                WHERE project_id = (SELECT id FROM projects WHERE name=@name)
                ORDER BY created_utc DESC, id DESC
                """,
                new { name = projectName });
        }

        /// <summary>
        /// Async helper for retrieving all snapshots for a given project name.
        /// Safe to call from UI code; uses true async DB access.
        /// </summary>
        public async Task<List<Snapshot>> GetSnapshotsForProjectAsync(string projectName, CancellationToken ct = default)
        {
            const string sql =
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id as ProjectId,
                  created_utc as CreatedUtc,
                  file_count as FileCount,
                  total_bytes as TotalBytes,
                  diff_added as DiffAdded,
                  diff_modified as DiffModified,
                  diff_deleted as DiffDeleted,
                  diff_net_bytes as DiffNetBytes,
                  diff_top_paths_json as DiffTopPathsJson
                FROM snapshots
                WHERE project_id = (SELECT id FROM projects WHERE name=@name)
                ORDER BY created_utc DESC, id DESC
                """;
            await using SqliteConnection c = Open();
            IEnumerable<Snapshot> rows = await c.QueryAsync<Snapshot>(
                new CommandDefinition(sql, new { name = projectName }, cancellationToken: ct)).ConfigureAwait(false);
            return [.. rows];
        }

        public IEnumerable<FileEntry> GetFilesForSnapshot(int snapshotId)
        {
            using SqliteConnection c = Open();
            IEnumerable<(string RelPath, long Size, string MTimeUtc, string HashSha256)> rows = c.Query<(string RelPath, long Size, string MTimeUtc, string HashSha256)>(
                "SELECT rel_path as RelPath, size as Size, mtime_utc as MTimeUtc, hash_sha256 as HashSha256 FROM files WHERE snapshot_id=@sid",
                new { sid = snapshotId });

            const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            foreach ((string relPath, long size, string mTimeUtc, string hashSha256) in rows)
            {
                if (!DateTime.TryParse(mTimeUtc, CultureInfo.InvariantCulture, styles, out DateTime mtime))
                    mtime = DateTime.SpecifyKind(DateTime.Parse(mTimeUtc), DateTimeKind.Utc);

                yield return new FileEntry(relPath, size, mtime, hashSha256);
            }
        }

        /// <summary>
        /// Async helper that materializes all file entries for a snapshot into a list
        /// using async DB access.
        /// </summary>
        public async Task<List<FileEntry>> GetFilesForSnapshotAsync(int snapshotId, CancellationToken ct = default)
        {
            const string sql =
                "SELECT rel_path as RelPath, size as Size, mtime_utc as MTimeUtc, hash_sha256 as HashSha256 FROM files WHERE snapshot_id=@sid";
            await using SqliteConnection c = Open();
            IEnumerable<(string RelPath, long Size, string MTimeUtc, string HashSha256)> rows = await c.QueryAsync<(string RelPath, long Size, string MTimeUtc, string HashSha256)>(
                new CommandDefinition(sql, new { sid = snapshotId }, cancellationToken: ct)).ConfigureAwait(false);
            const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            var list = new List<FileEntry>();
            foreach ((string relPath, long size, string mTimeUtc, string hashSha256) in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (!DateTime.TryParse(mTimeUtc, CultureInfo.InvariantCulture, styles, out DateTime mtime))
                    mtime = DateTime.SpecifyKind(DateTime.Parse(mTimeUtc), DateTimeKind.Utc);
                list.Add(new FileEntry(relPath, size, mtime, hashSha256));
            }

            return list;
        }

        public void InsertFiles(int snapshotId, IEnumerable<FileEntry> files)
        {
            using SqliteConnection c = Open();
            using SqliteTransaction tx = c.BeginTransaction();
            c.Execute(
                """
                INSERT INTO files(snapshot_id, rel_path, size, mtime_utc, hash_sha256)
                VALUES(@SnapshotId, @RelPath, @Size, @MTimeUtc, @HashSha256)
                """,
                files.Select(f => new
                {
                    SnapshotId = snapshotId,
                    f.RelPath,
                    f.Size,
                    MTimeUtc = f.MTimeUtc.ToString("u", CultureInfo.InvariantCulture),
                    f.HashSha256
                }),
                tx);
            tx.Commit();
        }

        public void UpdateFileHashes(int snapshotId, IEnumerable<(string RelPath, string HashSha256)> updates)
        {
            using SqliteConnection c = Open();
            using SqliteTransaction tx = c.BeginTransaction();

            c.Execute(
                """
                UPDATE files
                SET hash_sha256 = @HashSha256
                WHERE snapshot_id = @SnapshotId AND rel_path = @RelPath
                """,
                updates.Select(u => new
                {
                    SnapshotId = snapshotId,
                    u.RelPath,
                    u.HashSha256
                }),
                tx);

            tx.Commit();
        }
        // ---------- Backups ----------

        public int CreateBackup(
            int projectId,
            int snapshotId,
            string type,
            long totalBytes,
            string relativePath,
            string destinationPath,
            string destinationAlias,
            string backupMode = BackupModes.Full,
            bool isProtected = false,
            bool isEncrypted = false,
            string? cryptoDescriptorJson = null)
        {
            using SqliteConnection c = Open();
            string created = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);
            string externalId = NewExternalId();
            string originMachineName = Environment.MachineName;
            var descriptor = BackupCryptoDescriptor.FromMetadata(isEncrypted, cryptoDescriptorJson);
            string descriptorJson = descriptor.ToMetadataJson(isEncrypted);

            return c.ExecuteScalar<int>(
                """
                INSERT INTO backups(external_id, project_id, snapshot_id, created_utc, type, backup_mode, total_bytes, path, destination_path, destination_alias, origin_machine_name, is_protected, is_encrypted, crypto_descriptor_json, is_imported)
                VALUES(@ExternalId, @ProjectId, @SnapshotId, @CreatedUtc, @Type, @BackupMode, @TotalBytes, @Path, @DestinationPath, @DestinationAlias, @OriginMachineName, @IsProtected, @IsEncrypted, @CryptoDescriptorJson, @IsImported);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ExternalId     = externalId,
                    ProjectId       = projectId,
                    SnapshotId      = snapshotId,
                    CreatedUtc      = created,
                    Type            = type,
                    BackupMode      = BackupModes.Normalize(backupMode),
                    TotalBytes      = totalBytes,
                    Path            = relativePath,
                    DestinationPath = destinationPath ?? string.Empty,
                    DestinationAlias = destinationAlias ?? string.Empty,
                    OriginMachineName = originMachineName,
                    IsProtected     = isProtected ? 1 : 0,
                    IsEncrypted     = isEncrypted ? 1 : 0,
                    CryptoDescriptorJson = descriptorJson,
                    IsImported      = 0
                });
        }

        public int CreateBackupFromMetadata(
            string externalId,
            int projectId,
            int snapshotId,
            DateTime createdUtc,
            string type,
            long totalBytes,
            string relativePath,
            string destinationPath,
            string destinationAlias,
            bool isProtected,
            bool isImported,
            string backupMode = BackupModes.Full,
            string originMachineName = "",
            bool isEncrypted = false,
            string? cryptoDescriptorJson = null)
        {
            using SqliteConnection c = Open();
            string created = createdUtc.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
            string idToUse = string.IsNullOrWhiteSpace(externalId) ? NewExternalId() : externalId;
            var descriptor = BackupCryptoDescriptor.FromMetadata(isEncrypted, cryptoDescriptorJson);
            string descriptorJson = descriptor.ToMetadataJson(isEncrypted);
            return c.ExecuteScalar<int>(
                """
                INSERT INTO backups(external_id, project_id, snapshot_id, created_utc, type, backup_mode, total_bytes, path, destination_path, destination_alias, origin_machine_name, is_protected, is_encrypted, crypto_descriptor_json, is_imported)
                VALUES(@ExternalId, @ProjectId, @SnapshotId, @CreatedUtc, @Type, @BackupMode, @TotalBytes, @Path, @DestinationPath, @DestinationAlias, @OriginMachineName, @IsProtected, @IsEncrypted, @CryptoDescriptorJson, @IsImported);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ExternalId      = idToUse,
                    ProjectId       = projectId,
                    SnapshotId      = snapshotId,
                    CreatedUtc      = created,
                    Type            = type,
                    BackupMode      = BackupModes.Normalize(backupMode),
                    TotalBytes      = totalBytes,
                    Path            = relativePath ?? string.Empty,
                    DestinationPath = destinationPath ?? string.Empty,
                    DestinationAlias = destinationAlias ?? string.Empty,
                    OriginMachineName = originMachineName ?? string.Empty,
                    IsProtected     = isProtected ? 1 : 0,
                    IsEncrypted     = isEncrypted ? 1 : 0,
                    CryptoDescriptorJson = descriptorJson,
                    IsImported      = isImported ? 1 : 0
                });
        }

        public Backup? GetBackupByExternalId(string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return null;

            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<Backup>(
                """
                  SELECT
                    id,
                    external_id as ExternalId,
                    project_id  as ProjectId,
                    snapshot_id as SnapshotId,
                    created_utc as CreatedUtc,
                    type,
                    backup_mode as BackupMode,
                    total_bytes as TotalBytes,
                    path,
                    destination_path as DestinationPath,
                    destination_alias as DestinationAlias,
                    origin_machine_name as OriginMachineName,
                    is_protected as IsProtected,
                    is_encrypted as IsEncrypted,
                    crypto_descriptor_json as CryptoDescriptorJson,
                    is_imported as IsImported
                  FROM backups
                WHERE external_id = @externalId
                LIMIT 1;
                """,
                new { externalId });
        }

        public IReadOnlyDictionary<string, int> GetBackupExternalIdMap()
        {
            using SqliteConnection c = Open();
            IEnumerable<(int Id, string ExternalId)> rows = c.Query<(int Id, string ExternalId)>(
                "SELECT id as Id, external_id as ExternalId FROM backups WHERE external_id != '';");

            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ExternalId))
                .ToDictionary(row => row.ExternalId, row => row.Id, StringComparer.OrdinalIgnoreCase);
        }

        public void UpdateBackupExternalId(int backupId, string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return;

            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE backups SET external_id = @externalId WHERE id = @id;",
                new { externalId, id = backupId });
        }

        public void UpdateBackupTotalBytes(int backupId, long totalBytes)
        {
            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE backups SET total_bytes = @totalBytes WHERE id = @id;",
                new { totalBytes = Math.Max(0, totalBytes), id = backupId });
        }

        public void UpdateBackupProjectId(int backupId, int projectId)
        {
            using SqliteConnection c = Open();
            c.Execute(
                "UPDATE backups SET project_id = @projectId WHERE id = @id;",
                new { projectId, id = backupId });
        }

        public void UpdateBackupEncryptionMetadata(int backupId, bool isEncrypted, string? cryptoDescriptorJson, long totalBytes)
        {
            using SqliteConnection c = Open();
            var descriptor = BackupCryptoDescriptor.FromMetadata(isEncrypted, cryptoDescriptorJson);
            c.Execute(
                """
                UPDATE backups
                SET is_encrypted = @isEncrypted,
                    crypto_descriptor_json = @descriptorJson,
                    total_bytes = @totalBytes
                WHERE id = @id;
                """,
                new
                {
                    id = backupId,
                    isEncrypted = isEncrypted ? 1 : 0,
                    descriptorJson = descriptor.ToMetadataJson(isEncrypted),
                    totalBytes = Math.Max(0, totalBytes)
                });
        }

        public Backup? GetLatestBackupForProject(int projectId)
        {
            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<Backup>(
                """
                  SELECT
                    id,
                    external_id as ExternalId,
                    project_id  as ProjectId,
                    snapshot_id as SnapshotId,
                    created_utc as CreatedUtc,
                    type,
                    backup_mode as BackupMode,
                    total_bytes as TotalBytes,
                    path,
                    destination_path as DestinationPath,
                    destination_alias as DestinationAlias,
                    origin_machine_name as OriginMachineName,
                    is_protected as IsProtected,
                    is_encrypted as IsEncrypted,
                    crypto_descriptor_json as CryptoDescriptorJson,
                    is_imported as IsImported
                  FROM backups
                WHERE project_id = @pid
                ORDER BY created_utc DESC
                LIMIT 1;
                """,
                new { pid = projectId });
        }

        public List<Backup> GetLatestBackupsPerProject()
        {
            using SqliteConnection c = Open();
            return c.Query<Backup>(
                """
                  SELECT
                    b.id,
                    b.external_id as ExternalId,
                    b.project_id  as ProjectId,
                    b.snapshot_id as SnapshotId,
                    b.created_utc as CreatedUtc,
                    b.type,
                    b.backup_mode as BackupMode,
                    b.total_bytes as TotalBytes,
                    b.path,
                    b.destination_path as DestinationPath,
                    b.destination_alias as DestinationAlias,
                    b.origin_machine_name as OriginMachineName,
                    b.is_protected as IsProtected,
                    b.is_encrypted as IsEncrypted,
                    b.crypto_descriptor_json as CryptoDescriptorJson,
                    b.is_imported as IsImported
                  FROM backups b
                INNER JOIN (
                  SELECT project_id, MAX(created_utc) as created_utc
                  FROM backups
                  GROUP BY project_id
                ) latest
                ON b.project_id = latest.project_id AND b.created_utc = latest.created_utc
                ORDER BY b.created_utc DESC;
                """).AsList();
        }

        public Backup? GetBackupById(int backupId)
        {
            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<Backup>(
                """
                  SELECT
                    id,
                    external_id as ExternalId,
                    project_id  as ProjectId,
                    snapshot_id as SnapshotId,
                    created_utc as CreatedUtc,
                    type,
                    backup_mode as BackupMode,
                    total_bytes as TotalBytes,
                    path,
                    destination_path as DestinationPath,
                    destination_alias as DestinationAlias,
                    origin_machine_name as OriginMachineName,
                    is_protected as IsProtected,
                    is_encrypted as IsEncrypted,
                    crypto_descriptor_json as CryptoDescriptorJson,
                    is_imported as IsImported
                  FROM backups
                WHERE id = @id
                LIMIT 1;
                """,
                new { id = backupId });
        }

        public IReadOnlyList<Backup> GetRecentBackupsByProject(int limitPerProject)
        {
            if (limitPerProject <= 0)
                return [];

            using SqliteConnection c = Open();
            return c.Query<Backup>(
                """
                  SELECT
                    id,
                    external_id as ExternalId,
                    project_id  as ProjectId,
                    snapshot_id as SnapshotId,
                    created_utc as CreatedUtc,
                    type,
                    backup_mode as BackupMode,
                    total_bytes as TotalBytes,
                    path,
                    destination_path as DestinationPath,
                    destination_alias as DestinationAlias,
                    origin_machine_name as OriginMachineName,
                    is_protected as IsProtected,
                    is_encrypted as IsEncrypted,
                    crypto_descriptor_json as CryptoDescriptorJson,
                    is_imported as IsImported
                  FROM (
                    SELECT
                      b.*,
                    ROW_NUMBER() OVER (PARTITION BY project_id ORDER BY created_utc DESC) as rn
                  FROM backups b
                )
                WHERE rn <= @limit
                ORDER BY project_id, created_utc DESC;
                """,
                new { limit = limitPerProject }).AsList();
        }

        public IEnumerable<Backup> GetBackupsForProject(int projectId)
        {
            using SqliteConnection c = Open();
            return c.Query<Backup>(
                """
                  SELECT
                    id,
                    external_id as ExternalId,
                    project_id  as ProjectId,
                    snapshot_id as SnapshotId,
                    created_utc as CreatedUtc,
                    type,
                    backup_mode as BackupMode,
                    total_bytes as TotalBytes,
                    path,
                    destination_path as DestinationPath,
                    destination_alias as DestinationAlias,
                    origin_machine_name as OriginMachineName,
                    is_protected as IsProtected,
                    is_encrypted as IsEncrypted,
                    crypto_descriptor_json as CryptoDescriptorJson,
                    is_imported as IsImported
                  FROM backups
                  WHERE project_id = @pid
                ORDER BY created_utc DESC;
                """,
                new { pid = projectId });
        }

        public bool HasBackupForSnapshot(int projectId, int snapshotId)
        {
            using SqliteConnection c = Open();
            int? hit = c.QueryFirstOrDefault<int?>(
                "SELECT 1 FROM backups WHERE project_id = @pid AND snapshot_id = @sid LIMIT 1;",
                new { pid = projectId, sid = snapshotId });
            return hit.HasValue;
        }

        /// <summary>
        /// Async helper for retrieving all backups for a given project id.
        /// Safe to call from UI code; uses true async DB access.
        /// </summary>
        public async Task<List<Backup>> GetBackupsForProjectAsync(int projectId, CancellationToken ct = default)
        {
            const string sql =
                """
                  SELECT
                    id,
                    external_id as ExternalId,
                    project_id  as ProjectId,
                    snapshot_id as SnapshotId,
                    created_utc as CreatedUtc,
                    type,
                    backup_mode as BackupMode,
                    total_bytes as TotalBytes,
                    path,
                    destination_path as DestinationPath,
                    destination_alias as DestinationAlias,
                    origin_machine_name as OriginMachineName,
                    is_protected as IsProtected,
                    is_encrypted as IsEncrypted,
                    crypto_descriptor_json as CryptoDescriptorJson,
                    is_imported as IsImported
                  FROM backups
                  WHERE project_id = @pid
                ORDER BY created_utc DESC;
                """;
            await using SqliteConnection c = Open();
            IEnumerable<Backup> rows = await c.QueryAsync<Backup>(
                new CommandDefinition(sql, new { pid = projectId }, cancellationToken: ct)).ConfigureAwait(false);
            return [.. rows];
        }

        /// <summary>
        /// Backups created between the given UTC range (inclusive start and inclusive end).
        /// Used by the Backups UI for history and charts.
        /// </summary>
        public IEnumerable<Backup> GetBackupsInRange(DateTime fromUtc, DateTime toUtc)
        {
            using SqliteConnection c = Open();
            return c.Query<Backup>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  as ProjectId,
                  snapshot_id as SnapshotId,
                  created_utc as CreatedUtc,
                  type,
                  backup_mode as BackupMode,
                  total_bytes as TotalBytes,
                  path,
                  destination_path as DestinationPath,
                  destination_alias as DestinationAlias,
                  origin_machine_name as OriginMachineName,
                  is_protected as IsProtected,
                  is_encrypted as IsEncrypted,
                  crypto_descriptor_json as CryptoDescriptorJson,
                  is_imported as IsImported
                FROM backups
                WHERE created_utc >= @from AND created_utc <= @to
                ORDER BY created_utc DESC;
                """,
                new
                {
                    from = fromUtc.ToString("u", CultureInfo.InvariantCulture),
                    to   = toUtc.ToString("u", CultureInfo.InvariantCulture)
                });
        }

        /// <summary>
        /// Async helper for retrieving backups in a date range without blocking the UI thread.
        /// Uses true async DB access.
        /// </summary>
        public async Task<List<Backup>> GetBackupsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            const string sql =
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  as ProjectId,
                  snapshot_id as SnapshotId,
                  created_utc as CreatedUtc,
                  type,
                  backup_mode as BackupMode,
                  total_bytes as TotalBytes,
                  path,
                  destination_path as DestinationPath,
                  destination_alias as DestinationAlias,
                  origin_machine_name as OriginMachineName,
                  is_protected as IsProtected,
                  is_encrypted as IsEncrypted,
                  crypto_descriptor_json as CryptoDescriptorJson,
                  is_imported as IsImported
                FROM backups
                WHERE created_utc >= @from AND created_utc <= @to
                ORDER BY created_utc DESC;
                """;
            await using SqliteConnection c = Open();
            IEnumerable<Backup> rows = await c.QueryAsync<Backup>(
                new CommandDefinition(
                    sql,
                    new
                    {
                        from = fromUtc.ToString("u", CultureInfo.InvariantCulture),
                        to = toUtc.ToString("u", CultureInfo.InvariantCulture)
                    },
                    cancellationToken: ct)).ConfigureAwait(false);
            return [.. rows];
        }

        public Backup? GetLastBackup()
        {
            using SqliteConnection c = Open();
            return c.QueryFirstOrDefault<Backup>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  as ProjectId,
                  snapshot_id as SnapshotId,
                  created_utc as CreatedUtc,
                  type,
                  backup_mode as BackupMode,
                  total_bytes as TotalBytes,
                  path,
                  destination_path as DestinationPath,
                  destination_alias as DestinationAlias,
                  origin_machine_name as OriginMachineName,
                  is_protected as IsProtected,
                  is_encrypted as IsEncrypted,
                  crypto_descriptor_json as CryptoDescriptorJson,
                  is_imported as IsImported
                FROM backups
                ORDER BY created_utc DESC
                LIMIT 1;
                """);
        }

        public List<Backup> GetAllBackups()
        {
            using SqliteConnection c = Open();
            return c.Query<Backup>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  as ProjectId,
                  snapshot_id as SnapshotId,
                  created_utc as CreatedUtc,
                  type,
                  backup_mode as BackupMode,
                  total_bytes as TotalBytes,
                  path,
                  destination_path as DestinationPath,
                  destination_alias as DestinationAlias,
                  origin_machine_name as OriginMachineName,
                  is_protected as IsProtected,
                  is_encrypted as IsEncrypted,
                  crypto_descriptor_json as CryptoDescriptorJson,
                  is_imported as IsImported
                FROM backups
                ORDER BY created_utc DESC;
                """).AsList();
        }

        public int GetBackupCount()
        {
            using SqliteConnection c = Open();
            return c.ExecuteScalar<int>("SELECT COUNT(*) FROM backups;");
        }

        public IReadOnlyDictionary<int, long> GetBackupTotalsByProject(bool includeImported = false)
        {
            using SqliteConnection c = Open();
            IEnumerable<(int ProjectId, long TotalBytes)> rows = includeImported
                ? c.Query<(int ProjectId, long TotalBytes)>(
                    "SELECT project_id as ProjectId, COALESCE(SUM(total_bytes), 0) as TotalBytes FROM backups GROUP BY project_id;")
                : c.Query<(int ProjectId, long TotalBytes)>(
                    "SELECT project_id as ProjectId, COALESCE(SUM(total_bytes), 0) as TotalBytes FROM backups WHERE is_imported = 0 GROUP BY project_id;");

            return rows.ToDictionary(row => row.ProjectId, row => row.TotalBytes);
        }

        /// <summary>
        /// Repairs backup->project links using the authoritative snapshot->project link.
        /// Exact-match only: updates rows where backup.snapshot_id exists and points to a
        /// different project than backup.project_id.
        /// Returns the number of updated backup rows.
        /// </summary>
        public int RepairBackupProjectLinksFromSnapshots()
        {
            using SqliteConnection c = Open();
            return c.Execute(
                """
                UPDATE backups
                SET project_id = (
                    SELECT s.project_id
                    FROM snapshots s
                    WHERE s.id = backups.snapshot_id
                )
                WHERE EXISTS (
                    SELECT 1
                    FROM snapshots s
                    WHERE s.id = backups.snapshot_id
                      AND s.project_id != backups.project_id
                );
                """);
        }

        [SuppressMessage("Major Code Smell", "S1144:Unused private types or members", Justification = "Dapper populates this private row type by reflection.")]
        [SuppressMessage("Major Code Smell", "S3459:Unassigned members should be removed", Justification = "Dapper populates this private row type by reflection.")]
        private sealed class BackupActivityRow
        {
            public long ProjectId { get; init; }
            public string CreatedUtc { get; init; } = string.Empty;
            public string Type { get; init; } = string.Empty;
        }

        public List<(int projectId, DateTime createdUtc, string type)> GetRecentBackups(int limit)
        {
            using SqliteConnection c = Open();
            IEnumerable<BackupActivityRow> rows = c.Query<BackupActivityRow>(
                """
                SELECT project_id as ProjectId, created_utc as CreatedUtc, type as Type
                FROM backups
                ORDER BY created_utc DESC
                LIMIT @limit;
                """,
                new { limit });

            var result = new List<(int projectId, DateTime createdUtc, string type)>();
            foreach (BackupActivityRow row in rows)
            {
                if (!DateTime.TryParse(
                        row.CreatedUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime created))
                {
                    continue;
                }

                int projectId = row.ProjectId > int.MaxValue ? int.MaxValue : (int)row.ProjectId;
                result.Add((projectId, created, row.Type));
            }

            return result;
        }

        [SuppressMessage("Major Code Smell", "S1144:Unused private types or members", Justification = "Dapper populates this private row type by reflection.")]
        [SuppressMessage("Major Code Smell", "S3459:Unassigned members should be removed", Justification = "Dapper populates this private row type by reflection.")]
        private sealed class SnapshotActivityRow
        {
            public long ProjectId { get; init; }
            public string CreatedUtc { get; init; } = string.Empty;
        }

        public List<(int projectId, DateTime createdUtc)> GetRecentSnapshotsWithoutBackup(int limit)
        {
            using SqliteConnection c = Open();
            IEnumerable<SnapshotActivityRow> rows = c.Query<SnapshotActivityRow>(
                """
                SELECT s.project_id as ProjectId, s.created_utc as CreatedUtc
                FROM snapshots s
                LEFT JOIN backups b ON b.snapshot_id = s.id
                WHERE b.id IS NULL
                ORDER BY s.created_utc DESC
                LIMIT @limit;
                """,
                new { limit });

            var result = new List<(int projectId, DateTime createdUtc)>();
            foreach (SnapshotActivityRow row in rows)
            {
                if (!DateTime.TryParse(
                        row.CreatedUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime created))
                {
                    continue;
                }

                int projectId = row.ProjectId > int.MaxValue ? int.MaxValue : (int)row.ProjectId;
                result.Add((projectId, created));
            }

            return result;
        }

        public int AddRestoreHistoryEvent(RestoreHistoryEvent historyEvent)
        {
            if (historyEvent.ProjectId <= 0)
                throw new ArgumentOutOfRangeException(nameof(historyEvent), "Project id must be positive.");
            if (historyEvent.BackupId <= 0)
                throw new ArgumentOutOfRangeException(nameof(historyEvent), "Backup id must be positive.");
            if (historyEvent.SnapshotId <= 0)
                throw new ArgumentOutOfRangeException(nameof(historyEvent), "Snapshot id must be positive.");

            using SqliteConnection c = Open();
            string created = (historyEvent.CreatedUtc == default ? DateTime.UtcNow : historyEvent.CreatedUtc)
                .ToUniversalTime()
                .ToString("u", CultureInfo.InvariantCulture);

            return c.ExecuteScalar<int>(
                """
                INSERT INTO restore_history_events(
                  project_id,
                  backup_id,
                  snapshot_id,
                  created_utc,
                  restore_mode,
                  target_path,
                  status,
                  note)
                VALUES(
                  @ProjectId,
                  @BackupId,
                  @SnapshotId,
                  @CreatedUtc,
                  @RestoreMode,
                  @TargetPath,
                  @Status,
                  @Note);
                SELECT last_insert_rowid();
                """,
                new
                {
                    historyEvent.ProjectId,
                    historyEvent.BackupId,
                    historyEvent.SnapshotId,
                    CreatedUtc = created,
                    RestoreMode = ProjectRestoreMode.Normalize(historyEvent.RestoreMode),
                    TargetPath = historyEvent.TargetPath ?? string.Empty,
                    Status = RestoreHistoryEventStatus.Normalize(historyEvent.Status),
                    Note = historyEvent.Note ?? string.Empty
                });
        }

        public IReadOnlyList<RestoreHistoryEvent> GetRecentRestoreHistoryEvents(int limit)
        {
            using SqliteConnection c = Open();
            return [.. c.Query<RestoreHistoryEvent>(
                """
                SELECT
                  id as Id,
                  project_id as ProjectId,
                  backup_id as BackupId,
                  snapshot_id as SnapshotId,
                  created_utc as CreatedUtc,
                  restore_mode as RestoreMode,
                  target_path as TargetPath,
                  status as Status,
                  note as Note
                FROM restore_history_events
                ORDER BY created_utc DESC, id DESC
                LIMIT @limit;
                """,
                new { limit = Math.Max(0, limit) })];
        }

        [SuppressMessage("Major Code Smell", "S1144:Unused private types or members", Justification = "Dapper populates this private row type by reflection.")]
        [SuppressMessage("Major Code Smell", "S3459:Unassigned members should be removed", Justification = "Dapper populates this private row type by reflection.")]
        private sealed class BackupCountRow
        {
            public string Day { get; init; } = string.Empty;
            public long Count { get; init; }
        }

        [SuppressMessage("Major Code Smell", "S1144:Unused private types or members", Justification = "Dapper populates this private row type by reflection.")]
        [SuppressMessage("Major Code Smell", "S3459:Unassigned members should be removed", Justification = "Dapper populates this private row type by reflection.")]
        private sealed class BackupCountByTypeRow
        {
            public string Day { get; init; } = string.Empty;
            public string Type { get; init; } = string.Empty;
            public long IsImported { get; init; }
            public long Count { get; init; }
        }

        public IReadOnlyDictionary<DateTime, int> GetBackupCountsByDay(DateTime fromUtc, DateTime toUtc)
        {
            using SqliteConnection c = Open();
            IEnumerable<BackupCountRow> rows = c.Query<BackupCountRow>(
                """
                SELECT substr(created_utc, 1, 10) as Day, COUNT(*) as Count
                FROM backups
                WHERE created_utc >= @from AND created_utc <= @to
                GROUP BY Day
                ORDER BY Day;
                """,
                new
                {
                    from = fromUtc.ToString("u", CultureInfo.InvariantCulture),
                    to   = toUtc.ToString("u", CultureInfo.InvariantCulture)
                });

            var result = new Dictionary<DateTime, int>();
            foreach (BackupCountRow row in rows)
            {
                if (!DateTime.TryParseExact(
                        row.Day,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime day))
                {
                    continue;
                }

                if (row.Count > int.MaxValue)
                    result[day.Date] = int.MaxValue;
                else
                    result[day.Date] = (int)row.Count;
            }

            return result;
        }

        public IReadOnlyDictionary<DateTime, (int AutoCount, int ManualCount, int ImportedCount)> GetBackupCountsByDayBreakdown(DateTime fromUtc, DateTime toUtc)
        {
            using SqliteConnection c = Open();
            IEnumerable<BackupCountByTypeRow> rows = c.Query<BackupCountByTypeRow>(
                """
                SELECT substr(created_utc, 1, 10) as Day,
                       lower(type) as Type,
                       is_imported as IsImported,
                       COUNT(*) as Count
                FROM backups
                WHERE created_utc >= @from AND created_utc <= @to
                GROUP BY Day, Type, IsImported
                ORDER BY Day;
                """,
                new
                {
                    from = fromUtc.ToString("u", CultureInfo.InvariantCulture),
                    to   = toUtc.ToString("u", CultureInfo.InvariantCulture)
                });

            var result = new Dictionary<DateTime, (int AutoCount, int ManualCount, int ImportedCount)>();
            foreach (BackupCountByTypeRow row in rows)
            {
                if (!DateTime.TryParseExact(
                        row.Day,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime day))
                {
                    continue;
                }

                result.TryGetValue(day.Date, out (int AutoCount, int ManualCount, int ImportedCount) current);
                int autoCount = current.AutoCount;
                int manualCount = current.ManualCount;
                int importedCount = current.ImportedCount;

                int count = row.Count > int.MaxValue ? int.MaxValue : (int)row.Count;
                if (row.IsImported != 0)
                {
                    importedCount = Math.Min(int.MaxValue, importedCount + count);
                }
                else if (string.Equals(row.Type, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    autoCount = Math.Min(int.MaxValue, autoCount + count);
                }
                else
                {
                    manualCount = Math.Min(int.MaxValue, manualCount + count);
                }

                result[day.Date] = (autoCount, manualCount, importedCount);
            }

            return result;
        }

    public long GetTotalBackupBytes()
    {
        using SqliteConnection c = Open();
        return c.ExecuteScalar<long>("SELECT COALESCE(SUM(total_bytes), 0) FROM backups;");
    }

        public IReadOnlyDictionary<int, DateTime> GetLatestBackupUtcByProject()
        {
            using SqliteConnection c = Open();
            IEnumerable<(int ProjectId, string LatestUtc)> rows = c.Query<(int ProjectId, string LatestUtc)>(
                "SELECT project_id as ProjectId, MAX(created_utc) as LatestUtc FROM backups GROUP BY project_id;");

            var result = new Dictionary<int, DateTime>();
            foreach ((int projectId, string latestUtc) in rows)
            {
                if (string.IsNullOrWhiteSpace(latestUtc))
                    continue;

                if (DateTime.TryParse(
                        latestUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime parsed))
                {
                    result[projectId] = parsed;
                }
            }

            return result;
        }

        private static void EnsureColumnExists(SqliteConnection connection, string table, string column, string alterSql)
        {
            try
            {
                var cols = connection.Query($"PRAGMA table_info({table});")
                    .Select(row => (string)row.name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!cols.Contains(column))
                {
                    connection.Execute(alterSql);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SqliteRepository] Failed to ensure column {column} on {table}: {ex.Message}");
            }
        }

        private static string NewExternalId() => Guid.NewGuid().ToString("N");

        public (int autoCount, int manualCount) GetBackupTypeCounts()
        {
            using SqliteConnection c = Open();
            IEnumerable<(string type, int count)> rows = c.Query<(string type, int count)>(
                "SELECT type, COUNT(*) as count FROM backups GROUP BY type;");

            int auto = 0, manual = 0;
            foreach ((string type, int count) in rows)
            {
                if (string.Equals(type, "auto", StringComparison.OrdinalIgnoreCase))
                    auto = count;
                else if (string.Equals(type, "manual", StringComparison.OrdinalIgnoreCase))
                    manual = count;
            }

            return (auto, manual);
        }

        public void DeleteBackupById(int backupId)
        {
            using SqliteConnection c = Open();
            c.Execute("DELETE FROM backups WHERE id=@id;", new { id = backupId });
        }

        public void SetBackupProtection(int backupId, bool isProtected)
        {
            using SqliteConnection c = Open();
            using SqliteTransaction tx = c.BeginTransaction();

            int? snapshotId = c.QueryFirstOrDefault<int?>(
                "SELECT snapshot_id FROM backups WHERE id = @backupId;",
                new { backupId },
                tx);
            if (!snapshotId.HasValue)
                return;

            int protectedValue = isProtected ? 1 : 0;
            c.Execute(
                "UPDATE backups SET is_protected = @protectedValue WHERE snapshot_id = @snapshotId;",
                new { snapshotId = snapshotId.Value, protectedValue },
                tx);

            string now = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);
            c.Execute(
                """
                INSERT INTO snapshot_history_metadata(
                  snapshot_id,
                  label,
                  note,
                  tags,
                  is_protected,
                  is_known_good,
                  created_utc,
                  updated_utc)
                VALUES(
                  @snapshotId,
                  '',
                  '',
                  '',
                  @protectedValue,
                  0,
                  @now,
                  @now)
                ON CONFLICT(snapshot_id) DO UPDATE SET
                  is_protected = excluded.is_protected,
                  updated_utc = excluded.updated_utc;
                """,
                new { snapshotId = snapshotId.Value, protectedValue, now },
                tx);

            tx.Commit();
        }
    }

    public readonly record struct DeleteStats(int Projects, int Snapshots, int Files);
}
