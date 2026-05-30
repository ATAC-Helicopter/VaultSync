using System;
using VaultSync.Core.Models;

namespace VaultSync.Core.Tests.TestSupport;

public sealed class BackupBuilder
{
    private int _id = 10;
    private int _projectId = 1;
    private int _snapshotId = 11;
    private string _externalId = "backup-1";
    private DateTime _createdUtc = new(2026, 3, 13, 12, 0, 0, DateTimeKind.Utc);
    private string _type = "auto";
    private string _path = "vaultsync\\2026-03-13_12-00-00";
    private string _destinationPath = string.Empty;

    public BackupBuilder WithId(int id)
    {
        _id = id;
        _snapshotId = id;
        _externalId = $"backup-{id}";
        return this;
    }

    public BackupBuilder ForProject(int projectId)
    {
        _projectId = projectId;
        return this;
    }

    public BackupBuilder CreatedUtc(DateTime createdUtc)
    {
        _createdUtc = createdUtc;
        return this;
    }

    public BackupBuilder Manual()
    {
        _type = "manual";
        return this;
    }

    public BackupBuilder AtDestination(string destinationPath)
    {
        _destinationPath = destinationPath;
        return this;
    }

    public Backup Build() =>
        new()
        {
            Id = _id,
            ProjectId = _projectId,
            SnapshotId = _snapshotId,
            ExternalId = _externalId,
            CreatedUtc = _createdUtc,
            Type = _type,
            Path = _path,
            DestinationPath = _destinationPath
        };
}
