using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class Release187UpgradeCompatibilityTests
{
    [Fact]
    public void Exact187Configuration_LoadsWithoutResettingUserChoices()
    {
        using var root = new TempDirectory();
        using IDisposable scope = AppConfigStore.UseDirectoryForTests(root.Path);
        string dbPath = Path.Combine(root.Path, "vaultsync-1.8.7.db");
        File.WriteAllText(
            Path.Combine(root.Path, "appsettings.json"),
            $$"""
            {
              "ProjectsRoot": "{{Escape(root.Path)}}",
              "ResumeLastSession": false,
              "LastView": "History",
              "DbPath": "{{Escape(dbPath)}}",
              "Backups": {
                "EnableAutoBackups": false,
                "IntervalMinutes": 45,
                "MaxSnapshotsPerProject": 12,
                "AutoBackupDisabledProjects": [7, 9],
                "BackupRoot": "{{Escape(Path.Combine(root.Path, "backups"))}}",
                "UseCompression": true,
                "UseIncrementalBackups": true,
                "Encryption": {
                  "Enabled": true,
                  "KeyRef": "installation-local-key",
                  "Algorithm": "aes-256-cbc-hmac-sha256-v1",
                  "KdfProfile": "pbkdf2-sha256-v1",
                  "KdfParamRef": "pbkdf2-iter-210000"
                }
              }
            }
            """);

        AppConfig config = AppConfigStore.Load();

        Assert.Equal(root.Path, config.ProjectsRoot);
        Assert.False(config.ResumeLastSession);
        Assert.Equal("History", config.LastView);
        Assert.Equal(dbPath, config.DbPath);
        Assert.False(config.Backups.EnableAutoBackups);
        Assert.Equal(45, config.Backups.IntervalMinutes);
        Assert.Equal(12, config.Backups.MaxSnapshotsPerProject);
        Assert.Equal([7, 9], config.Backups.AutoBackupDisabledProjects);
        Assert.True(config.Backups.UseCompression);
        Assert.True(config.Backups.UseIncrementalBackups);
        Assert.True(config.Backups.Encryption.Enabled);
        Assert.Equal("installation-local-key", config.Backups.Encryption.KeyRef);
    }

    [Fact]
    public void Exact187Repository_OpensIdempotentlyAndPreservesUserState()
    {
        using var root = new TempDirectory();
        string dbPath = Path.Combine(root.Path, "vaultsync-1.8.7.db");
        CreateFrozen187Repository(dbPath);
        var repository = new SqliteRepository(dbPath);

        repository.EnsureSchema();
        repository.EnsureSchema();

        Project project = Assert.Single(repository.GetAllProjects());
        Assert.Equal("project-187", project.ExternalId);
        Assert.Equal("Project 1.8.7", project.Name);
        Assert.Equal("encrypted", project.EncryptionPolicy);
        Assert.Equal("direct", project.RestoreMode);
        Assert.Equal("always", project.VerificationPolicy);
        Assert.Equal("release,qualified", project.Tags);

        Snapshot snapshot = Assert.Single(repository.GetSnapshotsForProject(project.Name));
        Assert.Equal("snapshot-187", snapshot.ExternalId);
        Assert.Equal(1, snapshot.FileCount);
        Assert.Equal(128, snapshot.TotalBytes);
        FileEntry file = Assert.Single(repository.GetFilesForSnapshot(snapshot.Id));
        Assert.Equal("content.txt", file.RelPath);
        Assert.Equal(128, file.Size);
        Assert.Equal(new string('a', 64), file.HashSha256);

        Backup backup = Assert.Single(repository.GetBackupsForProject(project.Id));
        Assert.Equal("backup-187", backup.ExternalId);
        Assert.Equal(Path.Combine("project-187", "2026-08-21_10-00-00"), backup.Path);
        Assert.Equal("Primary", backup.DestinationAlias);
        Assert.True(backup.IsEncrypted);
        Assert.Contains("aes-256-cbc", backup.CryptoDescriptorJson, StringComparison.Ordinal);

        using var connection = Open(dbPath);
        Assert.Equal("preserve-me", connection.ExecuteScalar<string>("SELECT value FROM compatibility_marker LIMIT 1;"));
    }

    private static void CreateFrozen187Repository(string dbPath)
    {
        using SqliteConnection connection = Open(dbPath);
        connection.Execute("""
            PRAGMA foreign_keys = ON;
            CREATE TABLE project_groups(
              id TEXT PRIMARY KEY,
              name TEXT NOT NULL COLLATE NOCASE UNIQUE,
              sort_order INTEGER NOT NULL DEFAULT 0,
              created_utc TEXT NOT NULL
            );
            CREATE TABLE projects(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              external_id TEXT NOT NULL DEFAULT '',
              needs_restore INTEGER NOT NULL DEFAULT 0,
              preferred_destination_id TEXT,
              encryption_policy TEXT NOT NULL DEFAULT 'inherit',
              encryption_key_ref TEXT,
              restore_mode TEXT NOT NULL DEFAULT 'direct',
              verification_policy TEXT NOT NULL DEFAULT 'always',
              tags TEXT NOT NULL DEFAULT '',
              group_id TEXT,
              name TEXT NOT NULL UNIQUE,
              root_path TEXT NOT NULL,
              preset TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              FOREIGN KEY(group_id) REFERENCES project_groups(id) ON DELETE SET NULL
            );
            CREATE TABLE snapshots(
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
            CREATE TABLE files(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              snapshot_id INTEGER NOT NULL,
              rel_path TEXT NOT NULL,
              size INTEGER NOT NULL,
              mtime_utc TEXT NOT NULL,
              hash_sha256 TEXT NOT NULL,
              FOREIGN KEY(snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE
            );
            CREATE TABLE backups(
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
              is_protected INTEGER NOT NULL DEFAULT 0,
              is_encrypted INTEGER NOT NULL DEFAULT 0,
              crypto_descriptor_json TEXT NOT NULL DEFAULT '{}',
              is_imported INTEGER NOT NULL DEFAULT 0,
              FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE,
              FOREIGN KEY(snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE
            );
            CREATE TABLE compatibility_marker(value TEXT NOT NULL);

            INSERT INTO projects(
              id, external_id, needs_restore, preferred_destination_id,
              encryption_policy, encryption_key_ref, restore_mode,
              verification_policy, tags, name, root_path, preset, created_utc)
            VALUES(
              1, 'project-187', 0, 'destination-primary',
              'encrypted', 'installation-local-key', 'direct',
              'always', 'release,qualified', 'Project 1.8.7', '/projects/187',
              'generic', '2026-08-21T09:00:00.0000000Z');
            INSERT INTO snapshots(
              id, external_id, project_id, created_utc, file_count, total_bytes,
              diff_added, diff_modified, diff_deleted, diff_net_bytes, diff_top_paths_json)
            VALUES(
              1, 'snapshot-187', 1, '2026-08-21T10:00:00.0000000Z', 1, 128,
              1, 0, 0, 128, '["content.txt"]');
            INSERT INTO files(snapshot_id, rel_path, size, mtime_utc, hash_sha256)
            VALUES(1, 'content.txt', 128, '2026-08-21T10:00:00.0000000Z', @hash);
            INSERT INTO backups(
              id, external_id, project_id, snapshot_id, created_utc, type,
              backup_mode, total_bytes, path, destination_path, destination_alias,
              origin_machine_name, is_protected, is_encrypted,
              crypto_descriptor_json, is_imported)
            VALUES(
              1, 'backup-187', 1, 1, '2026-08-21T10:05:00.0000000Z', 'manual',
              'full', 128, 'project-187/2026-08-21_10-00-00', '/backups', 'Primary',
              'machine-187', 1, 1, '{"algorithm":"aes-256-cbc-hmac-sha256-v1"}', 0);
            INSERT INTO compatibility_marker(value) VALUES('preserve-me');
            """, new { hash = new string('a', 64) });
    }

    private static SqliteConnection Open(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
}
