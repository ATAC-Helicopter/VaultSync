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
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
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

    // Schema objects and indexes
    c.Execute("""
        -- Projects
        CREATE TABLE IF NOT EXISTS projects(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          name TEXT NOT NULL UNIQUE,
          root_path TEXT NOT NULL,
          preset TEXT NOT NULL,
          created_utc TEXT NOT NULL
        );

        -- Snapshots (cascade to files when a snapshot is deleted; cascade to snapshots when project is deleted)
        CREATE TABLE IF NOT EXISTS snapshots(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
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
          project_id INTEGER NOT NULL,
          snapshot_id INTEGER NOT NULL,
          created_utc TEXT NOT NULL,
          type TEXT NOT NULL,
          total_bytes INTEGER NOT NULL,
          path TEXT NOT NULL,
          FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE,
          FOREIGN KEY(snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE
        );

        -- Indexes (idempotent)
        CREATE INDEX IF NOT EXISTS idx_projects_name ON projects(name);

        CREATE INDEX IF NOT EXISTS idx_snapshots_project_created
          ON snapshots(project_id, created_utc DESC);

        CREATE INDEX IF NOT EXISTS idx_files_snapshot ON files(snapshot_id);

        CREATE INDEX IF NOT EXISTS idx_backups_project_created
          ON backups(project_id, created_utc DESC);

        CREATE INDEX IF NOT EXISTS idx_backups_created
          ON backups(created_utc DESC);

        -- Avoid duplicate file rows per snapshot (same logical path)
        CREATE UNIQUE INDEX IF NOT EXISTS ux_files_snapshot_rel
          ON files(snapshot_id, rel_path);
    """);
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
                "SELECT id, name, root_path as RootPath, preset as Preset, created_utc as CreatedUtc FROM projects WHERE name=@name",
                new { name });
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
                "SELECT id, name, root_path as RootPath, preset as Preset, created_utc as CreatedUtc FROM projects ORDER BY name");
        }

        /// <summary>
        /// Returns all projects in the database. This is a convenience wrapper used by the UI dashboard.
        /// </summary>
        public IEnumerable<Project> GetAllProjects()
        {
            // Delegate to the existing ListProjects implementation to keep one query definition.
            return ListProjects();
        }

        public int AddProject(Project p)
        {
            using var c = Open();
            return c.ExecuteScalar<int>(
                """
                INSERT INTO projects(name, root_path, preset, created_utc)
                VALUES(@Name, @RootPath, @Preset, @CreatedUtc);
                SELECT last_insert_rowid();
                """,
                p);
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
            return c.ExecuteScalar<int>(
                """
                INSERT INTO snapshots(project_id, created_utc, file_count, total_bytes)
                VALUES(@ProjectId, @CreatedUtc, @FileCount, @TotalBytes);
                SELECT last_insert_rowid();
                """,
                new { ProjectId = projectId, CreatedUtc = created, FileCount = fileCount, TotalBytes = totalBytes });
        }

        public Snapshot? GetLatestSnapshot(int projectId)
        {
            using var c = Open();
            return c.QueryFirstOrDefault<Snapshot>(
                "SELECT id, project_id as ProjectId, created_utc as CreatedUtc, file_count as FileCount, total_bytes as TotalBytes FROM snapshots WHERE project_id=@pid ORDER BY id DESC LIMIT 1",
                new { pid = projectId });
        }

        public Snapshot? GetLatestSnapshotForProject(int projectId)
        {
            using var c = Open();
            return c.QueryFirstOrDefault<Snapshot>(
                """
                SELECT
                  id,
                  project_id  AS ProjectId,
                  created_utc AS CreatedUtc,
                  file_count  AS FileCount,
                  total_bytes AS TotalBytes
                FROM snapshots
                WHERE project_id = @pid
                ORDER BY created_utc DESC
                LIMIT 1;
                """,
                new { pid = projectId });
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
                  project_id as ProjectId,
                  created_utc as CreatedUtc,
                  file_count as FileCount,
                  total_bytes as TotalBytes
                FROM snapshots
                ORDER BY created_utc DESC
                """);
        }

        public IEnumerable<Snapshot> GetSnapshotsForProject(string projectName)
        {
            using var c = Open();
            return c.Query<Snapshot>(
                """
                SELECT
                  id,
                  project_id as ProjectId,
                  created_utc as CreatedUtc,
                  file_count as FileCount,
                  total_bytes as TotalBytes
                FROM snapshots
                WHERE project_id = (SELECT id FROM projects WHERE name=@name)
                ORDER BY id DESC
                """,
                new { name = projectName });
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
        // ---------- Backups ----------

        public int CreateBackup(int projectId, int snapshotId, string type, long totalBytes, string relativePath)
        {
            using var c = Open();
            var created = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);

            return c.ExecuteScalar<int>(
                """
                INSERT INTO backups(project_id, snapshot_id, created_utc, type, total_bytes, path)
                VALUES(@ProjectId, @SnapshotId, @CreatedUtc, @Type, @TotalBytes, @Path);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ProjectId  = projectId,
                    SnapshotId = snapshotId,
                    CreatedUtc = created,
                    Type       = type,
                    TotalBytes = totalBytes,
                    Path       = relativePath
                });
        }

        public Backup? GetLatestBackupForProject(int projectId)
        {
            using var c = Open();
            return c.QueryFirstOrDefault<Backup>(
                """
                SELECT
                  id,
                  project_id  as ProjectId,
                  snapshot_id as SnapshotId,
                  created_utc as CreatedUtc,
                  type,
                  total_bytes as TotalBytes,
                  path
                FROM backups
                WHERE project_id = @pid
                ORDER BY created_utc DESC
                LIMIT 1;
                """,
                new { pid = projectId });
        }

        public IEnumerable<Backup> GetBackupsForProject(int projectId)
        {
            using var c = Open();
            return c.Query<Backup>(
                """
                SELECT
                  id,
                  project_id  as ProjectId,
                  snapshot_id as SnapshotId,
                  created_utc as CreatedUtc,
                  type,
                  total_bytes as TotalBytes,
                  path
                FROM backups
                WHERE project_id = @pid
                ORDER BY created_utc DESC;
                """,
                new { pid = projectId });
        }

        /// <summary>
        /// Backups created between the given UTC range (inclusive start, exclusive end).
        /// Used by the Backups UI for history and charts.
        /// </summary>
        public IEnumerable<Backup> GetBackupsInRange(DateTime fromUtc, DateTime toUtc)
        {
            using var c = Open();
            return c.Query<Backup>(
                """
                SELECT
                  id,
                  project_id  as ProjectId,
                  snapshot_id as SnapshotId,
                  created_utc as CreatedUtc,
                  type,
                  total_bytes as TotalBytes,
                  path
                FROM backups
                WHERE created_utc >= @from AND created_utc < @to
                ORDER BY created_utc DESC;
                """,
                new
                {
                    from = fromUtc.ToString("u", CultureInfo.InvariantCulture),
                    to   = toUtc.ToString("u", CultureInfo.InvariantCulture)
                });
        }

        public Backup? GetLastBackup()
        {
            using var c = Open();
            return c.QueryFirstOrDefault<Backup>(
                """
                SELECT
                  id,
                  project_id  as ProjectId,
                  snapshot_id as SnapshotId,
                  created_utc as CreatedUtc,
                  type,
                  total_bytes as TotalBytes,
                  path
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

        public long GetTotalBackupBytes()
        {
            using var c = Open();
            return c.ExecuteScalar<long>("SELECT COALESCE(SUM(total_bytes), 0) FROM backups;");
        }

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
    }

    public readonly record struct DeleteStats(int Projects, int Snapshots, int Files);
}