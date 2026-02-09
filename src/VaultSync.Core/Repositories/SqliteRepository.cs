using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Models;

namespace VaultSync.Core.Repositories
{
    public class SqliteRepository
    {
        private readonly string _dbPath;
        public SqliteRepository(string dbPath) => _dbPath = dbPath;

    private SqliteConnection Open()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=True");
        conn.Open();
        conn.Execute("PRAGMA foreign_keys = ON;");
        return conn;
    }
public (int Snapshots, int Files) DeleteSnapshotsById(string projectName, IEnumerable<int> snapshotIds)
{
    if (string.IsNullOrWhiteSpace(projectName))
        throw new ArgumentException("Project name is required", nameof(projectName));
    if (snapshotIds is null)
        throw new ArgumentNullException(nameof(snapshotIds));

    var ids = snapshotIds.Distinct().ToArray();
    if (ids.Length == 0)
        return (0, 0);

    using var conn = Open();                 // <-- uses your existing helper in this class
    using var tx   = conn.BeginTransaction();

    // Resolve project id
    var pid = conn.ExecuteScalar<int?>(
        "SELECT id FROM projects WHERE name = @name;",
        new { name = projectName }, tx);
    if (pid is null)
        throw new InvalidOperationException($"Project '{projectName}' not found");

    // Keep only snapshot ids that belong to this project
    var validIds = conn.Query<int>(
        @"SELECT id FROM snapshots 
          WHERE project_id = @pid AND id IN @ids 
          ORDER BY id;",
        new { pid, ids }, tx).ToArray();

    if (validIds.Length == 0)
    {
        tx.Commit();
        return (0, 0);
    }

    // Count files that will be removed via FK cascade
    var filesDeleted = conn.ExecuteScalar<int>(
        "SELECT COUNT(*) FROM files WHERE snapshot_id IN @ids;",
        new { ids = validIds }, tx);

    // Delete snapshots (files go away due to ON DELETE CASCADE)
    conn.Execute("DELETE FROM snapshots WHERE id IN @ids;", new { ids = validIds }, tx);

    tx.Commit();
    return (validIds.Length, filesDeleted);
}

 public void EnsureSchema()
{
    using var c = Open();

    // Ensure pragmas explicitly (keep also in Open(); harmless to repeat)
    c.Execute("PRAGMA foreign_keys = ON;");
    // journal_mode returns a value; read it to avoid driver complaints
    _ = c.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");

    // Schema objects
    c.Execute("""
        -- Projects
        CREATE TABLE IF NOT EXISTS projects(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          external_id TEXT NOT NULL DEFAULT '',
          needs_restore INTEGER NOT NULL DEFAULT 0,
          preferred_destination_id TEXT,
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
    """);

    // Migrations: add missing columns
    EnsureColumnExists("backups", "is_protected", "ALTER TABLE backups ADD COLUMN is_protected INTEGER NOT NULL DEFAULT 0;");
    EnsureColumnExists("backups", "is_imported", "ALTER TABLE backups ADD COLUMN is_imported INTEGER NOT NULL DEFAULT 0;");
    EnsureColumnExists("backups", "destination_path", "ALTER TABLE backups ADD COLUMN destination_path TEXT NOT NULL DEFAULT '';");
    EnsureColumnExists("backups", "destination_alias", "ALTER TABLE backups ADD COLUMN destination_alias TEXT NOT NULL DEFAULT '';");
    EnsureColumnExists("backups", "origin_machine_name", "ALTER TABLE backups ADD COLUMN origin_machine_name TEXT NOT NULL DEFAULT '';");
    EnsureColumnExists("backups", "is_encrypted", "ALTER TABLE backups ADD COLUMN is_encrypted INTEGER NOT NULL DEFAULT 0;");
    EnsureColumnExists("backups", "crypto_descriptor_json", "ALTER TABLE backups ADD COLUMN crypto_descriptor_json TEXT NOT NULL DEFAULT '{}';");
    EnsureColumnExists("projects", "external_id", "ALTER TABLE projects ADD COLUMN external_id TEXT NOT NULL DEFAULT '';");
    EnsureColumnExists("projects", "needs_restore", "ALTER TABLE projects ADD COLUMN needs_restore INTEGER NOT NULL DEFAULT 0;");
    EnsureColumnExists("projects", "preferred_destination_id", "ALTER TABLE projects ADD COLUMN preferred_destination_id TEXT;");
    EnsureColumnExists("snapshots", "external_id", "ALTER TABLE snapshots ADD COLUMN external_id TEXT NOT NULL DEFAULT '';");
    EnsureColumnExists("backups", "external_id", "ALTER TABLE backups ADD COLUMN external_id TEXT NOT NULL DEFAULT '';");

    // Indexes (idempotent)
    c.Execute("""
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

        -- Avoid duplicate file rows per snapshot (same logical path)
        CREATE UNIQUE INDEX IF NOT EXISTS ux_files_snapshot_rel
          ON files(snapshot_id, rel_path);
    """);

    // Normalize stored backup paths to the current OS separators for retention cleanup.
    NormalizeBackupPathSeparators(c);
}

private sealed class BackupPathRow
{
    public long Id { get; init; }
    public string Path { get; init; } = string.Empty;
}

private void NormalizeBackupPathSeparators(SqliteConnection connection)
{
    var rows = connection.Query<BackupPathRow>(
        "SELECT id, path FROM backups WHERE path LIKE '%\\\\%' OR path LIKE '%/%';").ToList();
    if (rows.Count == 0)
        return;

    var separator = Path.DirectorySeparatorChar;
    foreach (var row in rows)
    {
        if (string.IsNullOrWhiteSpace(row.Path))
            continue;

        var normalized = row.Path
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
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            using (var cmd = connection.CreateCommand())
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
            using var c = Open();
            return c.QueryFirstOrDefault<Project>(
                "SELECT id, external_id as ExternalId, needs_restore as NeedsRestore, preferred_destination_id as PreferredDestinationId, name, root_path as RootPath, preset as Preset, created_utc as CreatedUtc FROM projects WHERE name=@name",
                new { name });
        }

        public Project? GetProjectById(int id)
        {
            using var c = Open();
            return c.QueryFirstOrDefault<Project>(
                "SELECT id, external_id as ExternalId, needs_restore as NeedsRestore, preferred_destination_id as PreferredDestinationId, name, root_path as RootPath, preset as Preset, created_utc as CreatedUtc FROM projects WHERE id=@id",
                new { id });
        }

        public IReadOnlyDictionary<string, int> GetProjectExternalIdMap()
        {
            using var c = Open();
            var rows = c.Query<(int Id, string ExternalId)>(
                "SELECT id as Id, external_id as ExternalId FROM projects WHERE external_id != '';");

            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ExternalId))
                .ToDictionary(row => row.ExternalId, row => row.Id, StringComparer.OrdinalIgnoreCase);
        }

        public void RemoveProject(int projectId)
        {
            using var c = Open();

            // Because foreign_keys are ON and snapshots/files reference projects
            // with ON DELETE CASCADE, deleting the project row will also delete
            // its snapshots and files.
            const string sql = "DELETE FROM projects WHERE id = @id;";
            c.Execute(sql, new { id = projectId });
        }

        public IEnumerable<Project> ListProjects()
        {
            using var c = Open();
            return c.Query<Project>(
                "SELECT id, external_id as ExternalId, needs_restore as NeedsRestore, preferred_destination_id as PreferredDestinationId, name, root_path as RootPath, preset as Preset, created_utc as CreatedUtc FROM projects ORDER BY name");
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
        /// Intended for UI code; it simply wraps the existing synchronous method.
        /// </summary>
        public Task<List<Project>> GetAllProjectsAsync(CancellationToken ct = default)
        {
            return Task.Run(() => GetAllProjects().ToList(), ct);
        }

        public int AddProject(Project p)
        {
            using var c = Open();
            var externalId = string.IsNullOrWhiteSpace(p.ExternalId)
                ? NewExternalId()
                : p.ExternalId;
            return c.ExecuteScalar<int>(
                """
                INSERT INTO projects(external_id, needs_restore, preferred_destination_id, name, root_path, preset, created_utc)
                VALUES(@ExternalId, @NeedsRestore, @PreferredDestinationId, @Name, @RootPath, @Preset, @CreatedUtc);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ExternalId = externalId,
                    NeedsRestore = p.NeedsRestore ? 1 : 0,
                    PreferredDestinationId = string.IsNullOrWhiteSpace(p.PreferredDestinationId) ? null : p.PreferredDestinationId,
                    p.Name,
                    p.RootPath,
                    p.Preset,
                    CreatedUtc = p.CreatedUtc.ToString("u", CultureInfo.InvariantCulture)
                });
        }

        public void UpdateProjectNeedsRestore(int projectId, bool needsRestore)
        {
            using var c = Open();
            c.Execute(
                "UPDATE projects SET needs_restore = @needs WHERE id = @id;",
                new { needs = needsRestore ? 1 : 0, id = projectId });
        }

        public void UpdateProjectPreferredDestination(int projectId, string? preferredDestinationId)
        {
            using var c = Open();
            c.Execute(
                "UPDATE projects SET preferred_destination_id = @preferred WHERE id = @id;",
                new
                {
                    preferred = string.IsNullOrWhiteSpace(preferredDestinationId) ? null : preferredDestinationId,
                    id = projectId
                });
        }

        public Project? GetProjectByExternalId(string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return null;

            using var c = Open();
            return c.QueryFirstOrDefault<Project>(
                """
                SELECT id, external_id as ExternalId, preferred_destination_id as PreferredDestinationId, name, root_path as RootPath, preset as Preset, created_utc as CreatedUtc
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

            using var c = Open();
            c.Execute(
                "UPDATE projects SET external_id = @externalId WHERE id = @id;",
                new { externalId, id = projectId });
        }

        public bool UpdateProjectPath(string name, string newPath, out string? oldPath)
        {
            using var c = Open();
            var p = GetProjectByName(name);
            if (p is null) { oldPath = null; return false; }

            oldPath = p.RootPath;
            var rows = c.Execute(
                "UPDATE projects SET root_path=@newPath WHERE id=@id",
                new { newPath, id = p.Id });
            return rows > 0;
        }

        public DeleteStats DeleteProjectCascade(string name)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();

            var projId = c.ExecuteScalar<int>("SELECT id FROM projects WHERE name=@name", new { name }, tx);
            if (projId == 0)
                return new DeleteStats(0, 0, 0);

            var snaps = c.Query<int>("SELECT id FROM snapshots WHERE project_id=@pid", new { pid = projId }, tx).ToList();
            var filesCount = 0;
            if (snaps.Count > 0)
            {
                filesCount = c.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM files WHERE snapshot_id IN @sids",
                    new { sids = snaps }, tx);
            }

            // Delete snapshots; files will be removed automatically via ON DELETE CASCADE
            var snapsDeleted = c.Execute("DELETE FROM snapshots WHERE project_id=@pid", new { pid = projId }, tx);
            var projDeleted = c.Execute("DELETE FROM projects WHERE id=@pid", new { pid = projId }, tx);

            tx.Commit();
            return new DeleteStats(projDeleted, snapsDeleted, filesCount);
        }

        // ---------- Snapshots ----------
        public int CreateSnapshot(int projectId, long fileCount, long totalBytes)
        {
            using var c = Open();
            var created = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);
            var externalId = NewExternalId();
            return c.ExecuteScalar<int>(
                """
                INSERT INTO snapshots(external_id, project_id, created_utc, file_count, total_bytes)
                VALUES(@ExternalId, @ProjectId, @CreatedUtc, @FileCount, @TotalBytes);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ExternalId = externalId,
                    ProjectId = projectId,
                    CreatedUtc = created,
                    FileCount = fileCount,
                    TotalBytes = totalBytes
                });
        }

        public int CreateSnapshotFromMetadata(string externalId, int projectId, DateTime createdUtc, long fileCount, long totalBytes)
        {
            using var c = Open();
            var created = createdUtc.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
            var idToUse = string.IsNullOrWhiteSpace(externalId) ? NewExternalId() : externalId;
            return c.ExecuteScalar<int>(
                """
                INSERT INTO snapshots(external_id, project_id, created_utc, file_count, total_bytes)
                VALUES(@ExternalId, @ProjectId, @CreatedUtc, @FileCount, @TotalBytes);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ExternalId = idToUse,
                    ProjectId = projectId,
                    CreatedUtc = created,
                    FileCount = fileCount,
                    TotalBytes = totalBytes
                });
        }

        public Snapshot? GetSnapshotByExternalId(string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return null;

            using var c = Open();
            return c.QueryFirstOrDefault<Snapshot>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  AS ProjectId,
                  created_utc AS CreatedUtc,
                  file_count  AS FileCount,
                  total_bytes AS TotalBytes
                FROM snapshots
                WHERE external_id = @externalId
                LIMIT 1;
                """,
                new { externalId });
        }

        public Snapshot? GetSnapshotById(int id)
        {
            using var c = Open();
            return c.QueryFirstOrDefault<Snapshot>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  AS ProjectId,
                  created_utc AS CreatedUtc,
                  file_count  AS FileCount,
                  total_bytes AS TotalBytes
                FROM snapshots
                WHERE id = @id
                LIMIT 1;
                """,
                new { id });
        }

        public IReadOnlyDictionary<string, int> GetSnapshotExternalIdMap()
        {
            using var c = Open();
            var rows = c.Query<(int Id, string ExternalId)>(
                "SELECT id as Id, external_id as ExternalId FROM snapshots WHERE external_id != '';");

            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ExternalId))
                .ToDictionary(row => row.ExternalId, row => row.Id, StringComparer.OrdinalIgnoreCase);
        }

        public void UpdateSnapshotExternalId(int snapshotId, string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return;

            using var c = Open();
            c.Execute(
                "UPDATE snapshots SET external_id = @externalId WHERE id = @id;",
                new { externalId, id = snapshotId });
        }

        public Snapshot? GetLatestSnapshot(int projectId)
        {
            return GetLatestSnapshotForProject(projectId);
        }

        public Snapshot? GetLatestSnapshotForProject(int projectId)
        {
            using var c = Open();
            return c.QueryFirstOrDefault<Snapshot>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  AS ProjectId,
                  created_utc AS CreatedUtc,
                  file_count  AS FileCount,
                  total_bytes AS TotalBytes
                FROM snapshots
                WHERE project_id = @pid
                ORDER BY created_utc DESC, id DESC
                LIMIT 1;
                """,
                new { pid = projectId });
        }

        public IReadOnlyDictionary<int, (DateTime CreatedUtc, long TotalBytes)> GetLatestSnapshotInfoByProject()
        {
            using var c = Open();
            var rows = c.Query<(int ProjectId, string CreatedUtc, long TotalBytes)>(
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

            var styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            var map = new Dictionary<int, (DateTime CreatedUtc, long TotalBytes)>();
            foreach (var row in rows)
            {
                if (!DateTime.TryParse(row.CreatedUtc, CultureInfo.InvariantCulture, styles, out var created))
                    created = DateTime.SpecifyKind(DateTime.Parse(row.CreatedUtc), DateTimeKind.Utc);

                map[row.ProjectId] = (created, row.TotalBytes);
            }

            return map;
        }

        /// <summary>
        /// Returns all snapshots across all projects, newest first. Used by the UI dashboard.
        /// </summary>
        public IEnumerable<Snapshot> GetAllSnapshots()
        {
            using var c = Open();
            return c.Query<Snapshot>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id as ProjectId,
                  created_utc as CreatedUtc,
                  file_count as FileCount,
                  total_bytes as TotalBytes
                FROM snapshots
                ORDER BY created_utc DESC
                """);
        }

        /// <summary>
        /// Async helper for retrieving all snapshots without blocking the UI thread.
        /// Wraps the existing synchronous implementation.
        /// </summary>
        public Task<List<Snapshot>> GetAllSnapshotsAsync(CancellationToken ct = default)
        {
            return Task.Run(() => GetAllSnapshots().ToList(), ct);
        }

        public IEnumerable<Snapshot> GetSnapshotsForProject(string projectName)
        {
            using var c = Open();
            return c.Query<Snapshot>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id as ProjectId,
                  created_utc as CreatedUtc,
                  file_count as FileCount,
                  total_bytes as TotalBytes
                FROM snapshots
                WHERE project_id = (SELECT id FROM projects WHERE name=@name)
                ORDER BY created_utc DESC, id DESC
                """,
                new { name = projectName });
        }

        /// <summary>
        /// Async helper for retrieving all snapshots for a given project name.
        /// Safe to call from UI code; internally uses the existing sync method.
        /// </summary>
        public Task<List<Snapshot>> GetSnapshotsForProjectAsync(string projectName, CancellationToken ct = default)
        {
            return Task.Run(() => GetSnapshotsForProject(projectName).ToList(), ct);
        }

        public IEnumerable<FileEntry> GetFilesForSnapshot(int snapshotId)
        {
            using var c = Open();
            var rows = c.Query<(string RelPath, long Size, string MTimeUtc, string HashSha256)>(
                "SELECT rel_path as RelPath, size as Size, mtime_utc as MTimeUtc, hash_sha256 as HashSha256 FROM files WHERE snapshot_id=@sid",
                new { sid = snapshotId });

            var styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            foreach (var r in rows)
            {
                if (!DateTime.TryParse(r.MTimeUtc, CultureInfo.InvariantCulture, styles, out var mtime))
                    mtime = DateTime.SpecifyKind(DateTime.Parse(r.MTimeUtc), DateTimeKind.Utc);

                yield return new FileEntry(r.RelPath, r.Size, mtime, r.HashSha256);
            }
        }

        /// <summary>
        /// Async helper that materializes all file entries for a snapshot into a list
        /// on a background thread. This is safe to call from UI code without blocking
        /// the UI thread, and uses the existing synchronous implementation internally.
        /// </summary>
        public Task<List<FileEntry>> GetFilesForSnapshotAsync(int snapshotId, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                var list = new List<FileEntry>();
                foreach (var entry in GetFilesForSnapshot(snapshotId))
                {
                    ct.ThrowIfCancellationRequested();
                    list.Add(entry);
                }
                return list;
            }, ct);
        }

        public void InsertFiles(int snapshotId, IEnumerable<FileEntry> files)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
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
            using var c = Open();
            using var tx = c.BeginTransaction();

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
            bool isProtected = false,
            bool isEncrypted = false,
            string? cryptoDescriptorJson = null)
        {
            using var c = Open();
            var created = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);
            var externalId = NewExternalId();
            var originMachineName = Environment.MachineName;
            var descriptor = BackupCryptoDescriptor.FromMetadata(isEncrypted, cryptoDescriptorJson);
            var descriptorJson = descriptor.ToMetadataJson(isEncrypted);

            return c.ExecuteScalar<int>(
                """
                INSERT INTO backups(external_id, project_id, snapshot_id, created_utc, type, total_bytes, path, destination_path, destination_alias, origin_machine_name, is_protected, is_encrypted, crypto_descriptor_json, is_imported)
                VALUES(@ExternalId, @ProjectId, @SnapshotId, @CreatedUtc, @Type, @TotalBytes, @Path, @DestinationPath, @DestinationAlias, @OriginMachineName, @IsProtected, @IsEncrypted, @CryptoDescriptorJson, @IsImported);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ExternalId     = externalId,
                    ProjectId       = projectId,
                    SnapshotId      = snapshotId,
                    CreatedUtc      = created,
                    Type            = type,
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
            string originMachineName = "",
            bool isEncrypted = false,
            string? cryptoDescriptorJson = null)
        {
            using var c = Open();
            var created = createdUtc.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
            var idToUse = string.IsNullOrWhiteSpace(externalId) ? NewExternalId() : externalId;
            var descriptor = BackupCryptoDescriptor.FromMetadata(isEncrypted, cryptoDescriptorJson);
            var descriptorJson = descriptor.ToMetadataJson(isEncrypted);
            return c.ExecuteScalar<int>(
                """
                INSERT INTO backups(external_id, project_id, snapshot_id, created_utc, type, total_bytes, path, destination_path, destination_alias, origin_machine_name, is_protected, is_encrypted, crypto_descriptor_json, is_imported)
                VALUES(@ExternalId, @ProjectId, @SnapshotId, @CreatedUtc, @Type, @TotalBytes, @Path, @DestinationPath, @DestinationAlias, @OriginMachineName, @IsProtected, @IsEncrypted, @CryptoDescriptorJson, @IsImported);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ExternalId      = idToUse,
                    ProjectId       = projectId,
                    SnapshotId      = snapshotId,
                    CreatedUtc      = created,
                    Type            = type,
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

            using var c = Open();
            return c.QueryFirstOrDefault<Backup>(
                """
                  SELECT
                    id,
                    external_id as ExternalId,
                    project_id  as ProjectId,
                    snapshot_id as SnapshotId,
                    created_utc as CreatedUtc,
                    type,
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
            using var c = Open();
            var rows = c.Query<(int Id, string ExternalId)>(
                "SELECT id as Id, external_id as ExternalId FROM backups WHERE external_id != '';");

            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ExternalId))
                .ToDictionary(row => row.ExternalId, row => row.Id, StringComparer.OrdinalIgnoreCase);
        }

        public void UpdateBackupExternalId(int backupId, string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return;

            using var c = Open();
            c.Execute(
                "UPDATE backups SET external_id = @externalId WHERE id = @id;",
                new { externalId, id = backupId });
        }

        public Backup? GetLatestBackupForProject(int projectId)
        {
            using var c = Open();
            return c.QueryFirstOrDefault<Backup>(
                """
                  SELECT
                    id,
                    external_id as ExternalId,
                    project_id  as ProjectId,
                    snapshot_id as SnapshotId,
                    created_utc as CreatedUtc,
                    type,
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
            using var c = Open();
            return c.Query<Backup>(
                """
                  SELECT
                    b.id,
                    b.external_id as ExternalId,
                    b.project_id  as ProjectId,
                    b.snapshot_id as SnapshotId,
                    b.created_utc as CreatedUtc,
                    b.type,
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
                """).ToList();
        }

        public Backup? GetBackupById(int backupId)
        {
            using var c = Open();
            return c.QueryFirstOrDefault<Backup>(
                """
                  SELECT
                    id,
                    external_id as ExternalId,
                    project_id  as ProjectId,
                    snapshot_id as SnapshotId,
                    created_utc as CreatedUtc,
                    type,
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
                return Array.Empty<Backup>();

            using var c = Open();
            return c.Query<Backup>(
                """
                  SELECT
                    id,
                    external_id as ExternalId,
                    project_id  as ProjectId,
                    snapshot_id as SnapshotId,
                    created_utc as CreatedUtc,
                    type,
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
                new { limit = limitPerProject }).ToList();
        }

        public IEnumerable<Backup> GetBackupsForProject(int projectId)
        {
            using var c = Open();
            return c.Query<Backup>(
                """
                  SELECT
                    id,
                    external_id as ExternalId,
                    project_id  as ProjectId,
                    snapshot_id as SnapshotId,
                    created_utc as CreatedUtc,
                    type,
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
            using var c = Open();
            var hit = c.QueryFirstOrDefault<int?>(
                "SELECT 1 FROM backups WHERE project_id = @pid AND snapshot_id = @sid LIMIT 1;",
                new { pid = projectId, sid = snapshotId });
            return hit.HasValue;
        }

        /// <summary>
        /// Async helper for retrieving all backups for a given project id.
        /// Safe to call from UI code; internally uses the existing sync method.
        /// </summary>
        public Task<List<Backup>> GetBackupsForProjectAsync(int projectId, CancellationToken ct = default)
        {
            return Task.Run(() => GetBackupsForProject(projectId).ToList(), ct);
        }

        /// <summary>
        /// Backups created between the given UTC range (inclusive start and inclusive end).
        /// Used by the Backups UI for history and charts.
        /// </summary>
        public IEnumerable<Backup> GetBackupsInRange(DateTime fromUtc, DateTime toUtc)
        {
            using var c = Open();
            return c.Query<Backup>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  as ProjectId,
                  snapshot_id as SnapshotId,
                  created_utc as CreatedUtc,
                  type,
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
        /// Wraps the existing synchronous implementation.
        /// </summary>
        public Task<List<Backup>> GetBackupsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            return Task.Run(() => GetBackupsInRange(fromUtc, toUtc).ToList(), ct);
        }

        public Backup? GetLastBackup()
        {
            using var c = Open();
            return c.QueryFirstOrDefault<Backup>(
                """
                SELECT
                  id,
                  external_id as ExternalId,
                  project_id  as ProjectId,
                  snapshot_id as SnapshotId,
                  created_utc as CreatedUtc,
                  type,
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

        public int GetBackupCount()
        {
            using var c = Open();
            return c.ExecuteScalar<int>("SELECT COUNT(*) FROM backups;");
        }

        public IReadOnlyDictionary<int, long> GetBackupTotalsByProject(bool includeImported = false)
        {
            using var c = Open();
            var rows = includeImported
                ? c.Query<(int ProjectId, long TotalBytes)>(
                    "SELECT project_id as ProjectId, COALESCE(SUM(total_bytes), 0) as TotalBytes FROM backups GROUP BY project_id;")
                : c.Query<(int ProjectId, long TotalBytes)>(
                    "SELECT project_id as ProjectId, COALESCE(SUM(total_bytes), 0) as TotalBytes FROM backups WHERE is_imported = 0 GROUP BY project_id;");

            return rows.ToDictionary(row => row.ProjectId, row => row.TotalBytes);
        }

        private sealed class BackupActivityRow
        {
            public long ProjectId { get; init; }
            public string CreatedUtc { get; init; } = string.Empty;
            public string Type { get; init; } = string.Empty;
        }

        public List<(int projectId, DateTime createdUtc, string type)> GetRecentBackups(int limit)
        {
            using var c = Open();
            var rows = c.Query<BackupActivityRow>(
                """
                SELECT project_id as ProjectId, created_utc as CreatedUtc, type as Type
                FROM backups
                ORDER BY created_utc DESC
                LIMIT @limit;
                """,
                new { limit });

            var result = new List<(int projectId, DateTime createdUtc, string type)>();
            foreach (var row in rows)
            {
                if (!DateTime.TryParse(
                        row.CreatedUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var created))
                {
                    continue;
                }

                var projectId = row.ProjectId > int.MaxValue ? int.MaxValue : (int)row.ProjectId;
                result.Add((projectId, created, row.Type));
            }

            return result;
        }

        private sealed class SnapshotActivityRow
        {
            public long ProjectId { get; init; }
            public string CreatedUtc { get; init; } = string.Empty;
        }

        public List<(int projectId, DateTime createdUtc)> GetRecentSnapshotsWithoutBackup(int limit)
        {
            using var c = Open();
            var rows = c.Query<SnapshotActivityRow>(
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
            foreach (var row in rows)
            {
                if (!DateTime.TryParse(
                        row.CreatedUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var created))
                {
                    continue;
                }

                var projectId = row.ProjectId > int.MaxValue ? int.MaxValue : (int)row.ProjectId;
                result.Add((projectId, created));
            }

            return result;
        }

        private sealed class BackupCountRow
        {
            public string Day { get; init; } = string.Empty;
            public long Count { get; init; }
        }

        private sealed class BackupCountByTypeRow
        {
            public string Day { get; init; } = string.Empty;
            public string Type { get; init; } = string.Empty;
            public long IsImported { get; init; }
            public long Count { get; init; }
        }

        public IReadOnlyDictionary<DateTime, int> GetBackupCountsByDay(DateTime fromUtc, DateTime toUtc)
        {
            using var c = Open();
            var rows = c.Query<BackupCountRow>(
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
            foreach (var row in rows)
            {
                if (!DateTime.TryParseExact(
                        row.Day,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var day))
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
            using var c = Open();
            var rows = c.Query<BackupCountByTypeRow>(
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
            foreach (var row in rows)
            {
                if (!DateTime.TryParseExact(
                        row.Day,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var day))
                {
                    continue;
                }

                result.TryGetValue(day.Date, out var current);
                var autoCount = current.AutoCount;
                var manualCount = current.ManualCount;
                var importedCount = current.ImportedCount;

                var count = row.Count > int.MaxValue ? int.MaxValue : (int)row.Count;
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
        using var c = Open();
        return c.ExecuteScalar<long>("SELECT COALESCE(SUM(total_bytes), 0) FROM backups;");
    }

        public IReadOnlyDictionary<int, DateTime> GetLatestBackupUtcByProject()
        {
            using var c = Open();
            var rows = c.Query<(int ProjectId, string LatestUtc)>(
                "SELECT project_id as ProjectId, MAX(created_utc) as LatestUtc FROM backups GROUP BY project_id;");

            var result = new Dictionary<int, DateTime>();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.LatestUtc))
                    continue;

                if (DateTime.TryParse(
                        row.LatestUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    result[row.ProjectId] = parsed;
                }
            }

            return result;
        }

    private void EnsureColumnExists(string table, string column, string alterSql)
    {
        try
        {
            using var c = Open();
            var cols = c.Query($"PRAGMA table_info({table});")
                        .Select(row => (string)row.name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!cols.Contains(column))
            {
                c.Execute(alterSql);
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
            using var c = Open();
            var rows = c.Query<(string type, int count)>(
                "SELECT type, COUNT(*) as count FROM backups GROUP BY type;");

            int auto = 0, manual = 0;
            foreach (var row in rows)
            {
                if (string.Equals(row.type, "auto", StringComparison.OrdinalIgnoreCase))
                    auto = row.count;
                else if (string.Equals(row.type, "manual", StringComparison.OrdinalIgnoreCase))
                    manual = row.count;
            }

            return (auto, manual);
        }

        public void DeleteBackupById(int backupId)
        {
            using var c = Open();
            c.Execute("DELETE FROM backups WHERE id=@id;", new { id = backupId });
        }

        public void SetBackupProtection(int backupId, bool isProtected)
        {
            using var c = Open();
            c.Execute("UPDATE backups SET is_protected=@p WHERE id=@id;", new { id = backupId, p = isProtected ? 1 : 0 });
        }
    }

    public readonly record struct DeleteStats(int Projects, int Snapshots, int Files);
}
