using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Benchmarks;

BenchmarkOptions options = BenchmarkOptions.Parse(args);
string fixtureRoot = Path.Combine(Path.GetTempPath(), $"vaultsync-benchmark-{Guid.NewGuid():N}");
Directory.CreateDirectory(fixtureRoot);

try
{
    FileEntry[] olderFiles = CreateFiles(options.FileCount, modified: false);
    FileEntry[] newerFiles = CreateFiles(options.FileCount, modified: true);
    string databasePath = Path.Combine(fixtureRoot, "large-history.db");
    SqliteRepository repository = CreateHistoryFixture(databasePath, options.HistoryEvents);

    _ = SnapshotCompareService.Compare(olderFiles, newerFiles);
    _ = ReadHistory(repository);

    BenchmarkMeasurement compare = Measure(
        "snapshot-compare",
        options.Iterations,
        () => SnapshotCompareService.Compare(olderFiles, newerFiles).ChangedCount,
        maxP95Milliseconds: 500,
        maxP95AllocatedBytes: 64L * 1024 * 1024);
    BenchmarkMeasurement history = Measure(
        "large-history-read",
        options.Iterations,
        () => ReadHistory(repository),
        maxP95Milliseconds: 500,
        maxP95AllocatedBytes: 48L * 1024 * 1024);
    BenchmarkMeasurement cancellation = await MeasureCancellationAsync(
        olderFiles,
        newerFiles,
        maxP95Milliseconds: 250);

    var report = new BenchmarkReport(
        SchemaVersion: 1,
        RecordedUtc: DateTime.UtcNow,
        SourceCommit: ResolveSourceCommit(),
        Configuration: "Release",
        Machine: new MachineProfile(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            GCSettings.IsServerGC),
        Fixture: new FixtureProfile(options.HistoryEvents, options.FileCount, options.Iterations),
        Measurements: [compare, history, cancellation]);

    string json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    });
    Console.WriteLine(json);

    if (!string.IsNullOrWhiteSpace(options.OutputPath))
    {
        string outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, json + Environment.NewLine);
    }

    if (options.Enforce && report.Measurements.Any(measurement => !measurement.Passed))
    {
        await Console.Error.WriteLineAsync("One or more VaultSync performance budgets failed.");
        return 1;
    }

    return 0;
}
finally
{
    try
    {
        Directory.Delete(fixtureRoot, recursive: true);
    }
    catch (IOException)
    {
        // A benchmark result is still useful if temporary-file cleanup is delayed.
    }
}

static FileEntry[] CreateFiles(int count, bool modified)
{
    DateTime timestamp = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
    return Enumerable.Range(0, count)
        .Select(index => new FileEntry(
            $"project/{index % 128:D3}/file-{index:D7}.bin",
            modified && index % 10 == 0 ? 4_097 : 4_096,
            timestamp,
            modified && index % 10 == 0 ? $"changed-{index}" : $"stable-{index}"))
        .ToArray();
}

static SqliteRepository CreateHistoryFixture(string databasePath, int historyEvents)
{
    const string ExternalParameter = "$external";
    const string CreatedParameter = "$created";
    const string ProjectParameter = "$project";

    var repository = new SqliteRepository(databasePath);
    repository.EnsureSchema();

    var connectionBuilder = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false
    };
    using var connection = new SqliteConnection(connectionBuilder.ConnectionString);
    connection.Open();
    using SqliteTransaction transaction = connection.BeginTransaction();
    using SqliteCommand projectCommand = connection.CreateCommand();
    projectCommand.Transaction = transaction;
    projectCommand.CommandText =
        "INSERT INTO projects(external_id, name, root_path, preset, created_utc) VALUES($external, $name, $root, 'dotnet', $created); SELECT last_insert_rowid();";
    projectCommand.Parameters.Add(ExternalParameter, SqliteType.Text);
    projectCommand.Parameters.Add("$name", SqliteType.Text);
    projectCommand.Parameters.Add("$root", SqliteType.Text);
    projectCommand.Parameters.Add(CreatedParameter, SqliteType.Text);

    using SqliteCommand snapshotCommand = connection.CreateCommand();
    snapshotCommand.Transaction = transaction;
    snapshotCommand.CommandText =
        "INSERT INTO snapshots(external_id, project_id, created_utc, file_count, total_bytes) VALUES($external, $project, $created, 100000, 409600000); SELECT last_insert_rowid();";
    snapshotCommand.Parameters.Add(ExternalParameter, SqliteType.Text);
    snapshotCommand.Parameters.Add(ProjectParameter, SqliteType.Integer);
    snapshotCommand.Parameters.Add(CreatedParameter, SqliteType.Text);

    using SqliteCommand backupCommand = connection.CreateCommand();
    backupCommand.Transaction = transaction;
    backupCommand.CommandText =
        "INSERT INTO backups(external_id, project_id, snapshot_id, created_utc, type, total_bytes, path, destination_path, destination_alias, origin_machine_name) VALUES($external, $project, $snapshot, $created, 'automatic', 409600000, $path, '/benchmark', 'Benchmark', 'fixture');";
    backupCommand.Parameters.Add(ExternalParameter, SqliteType.Text);
    backupCommand.Parameters.Add(ProjectParameter, SqliteType.Integer);
    backupCommand.Parameters.Add("$snapshot", SqliteType.Integer);
    backupCommand.Parameters.Add(CreatedParameter, SqliteType.Text);
    backupCommand.Parameters.Add("$path", SqliteType.Text);

    const int projectCount = 25;
    int eventsPerProject = (int)Math.Ceiling((double)historyEvents / projectCount);
    int createdEvents = 0;
    for (int projectIndex = 0; projectIndex < projectCount && createdEvents < historyEvents; projectIndex++)
    {
        projectCommand.Parameters[ExternalParameter].Value = $"project-{projectIndex:D2}";
        projectCommand.Parameters["$name"].Value = $"Benchmark Project {projectIndex:D2}";
        projectCommand.Parameters["$root"].Value = $"/benchmark/project-{projectIndex:D2}";
        projectCommand.Parameters[CreatedParameter].Value = "2026-01-01 00:00:00Z";
        long projectId = (long)(projectCommand.ExecuteScalar() ?? 0L);

        for (int eventIndex = 0; eventIndex < eventsPerProject && createdEvents < historyEvents; eventIndex++, createdEvents++)
        {
            DateTime createdUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(createdEvents);
            string created = createdUtc.ToString("u");
            snapshotCommand.Parameters[ExternalParameter].Value = $"snapshot-{createdEvents:D6}";
            snapshotCommand.Parameters[ProjectParameter].Value = projectId;
            snapshotCommand.Parameters[CreatedParameter].Value = created;
            long snapshotId = (long)(snapshotCommand.ExecuteScalar() ?? 0L);

            backupCommand.Parameters[ExternalParameter].Value = $"backup-{createdEvents:D6}";
            backupCommand.Parameters[ProjectParameter].Value = projectId;
            backupCommand.Parameters["$snapshot"].Value = snapshotId;
            backupCommand.Parameters[CreatedParameter].Value = created;
            backupCommand.Parameters["$path"].Value = $"project-{projectIndex:D2}/backup-{createdEvents:D6}.zip";
            backupCommand.ExecuteNonQuery();
        }
    }

    transaction.Commit();
    return repository;
}

static int ReadHistory(SqliteRepository repository)
{
    List<Project> projects = repository.GetAllProjects().ToList();
    List<Backup> allBackups = repository.GetAllBackups()
        .OrderByDescending(backup => backup.CreatedUtc)
        .ThenByDescending(backup => backup.Id)
        .ToList();
    List<Backup> backups = allBackups.Take(60).ToList();
    HashSet<int> backupSnapshotIds = allBackups.Select(backup => backup.SnapshotId).ToHashSet();
    List<Snapshot> allSnapshots = repository.GetAllSnapshots().ToList();
    List<Snapshot> snapshotOnly = allSnapshots
        .Where(snapshot => !backupSnapshotIds.Contains(snapshot.Id))
        .OrderByDescending(snapshot => snapshot.CreatedUtc)
        .ThenByDescending(snapshot => snapshot.Id)
        .Take(20)
        .ToList();
    IReadOnlyDictionary<int, SnapshotHistoryMetadata> metadata =
        repository.GetSnapshotHistoryMetadataBySnapshotIds(
            backups.Select(backup => backup.SnapshotId)
                .Concat(snapshotOnly.Select(snapshot => snapshot.Id)));

    return projects.Count + allBackups.Count + allSnapshots.Count + metadata.Count;
}

static BenchmarkMeasurement Measure(
    string name,
    int iterations,
    Func<int> action,
    double maxP95Milliseconds,
    long maxP95AllocatedBytes)
{
    var durations = new List<double>(iterations);
    var allocations = new List<long>(iterations);
    int checksum = 0;
    for (int iteration = 0; iteration < iterations; iteration++)
    {
#pragma warning disable S1215 // Forced collections isolate per-iteration allocation measurements in this benchmark harness.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
#pragma warning restore S1215
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        checksum ^= action();
        durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        allocations.Add(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
    }

    GC.KeepAlive(checksum);
    double p50 = Percentile(durations, 0.50);
    double p95 = Percentile(durations, 0.95);
    long allocatedP95 = (long)Percentile(allocations.Select(value => (double)value).ToList(), 0.95);
    return new BenchmarkMeasurement(
        name,
        p50,
        p95,
        durations.Max(),
        allocatedP95,
        maxP95Milliseconds,
        maxP95AllocatedBytes,
        p95 <= maxP95Milliseconds && allocatedP95 <= maxP95AllocatedBytes);
}

static async Task<BenchmarkMeasurement> MeasureCancellationAsync(
    FileEntry[] olderFiles,
    FileEntry[] newerFiles,
    double maxP95Milliseconds)
{
    const int iterations = 5;
    var durations = new List<double>(iterations);
    bool allIterationsObservedCancellation = true;
    for (int iteration = 0; iteration < iterations; iteration++)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task compare = Task.Factory.StartNew(
            () =>
            {
                started.TrySetResult();
                SnapshotCompareService.Compare(
                    olderFiles,
                    newerFiles,
                    cancellationToken: cancellation.Token);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        await started.Task;
        // A dedicated worker prevents a busy comparison from starving the timer that
        // requests cancellation on small hosted runners.
        await Task.Delay(5);
        long cancelledAt = Stopwatch.GetTimestamp();
        await cancellation.CancelAsync();
        bool cancellationObserved = false;
        try
        {
            await compare;
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }

        allIterationsObservedCancellation &= cancellationObserved;
        durations.Add(Stopwatch.GetElapsedTime(cancelledAt).TotalMilliseconds);
    }

    double p50 = Percentile(durations, 0.50);
    double p95 = Percentile(durations, 0.95);
    return new BenchmarkMeasurement(
        "snapshot-compare-cancellation",
        p50,
        p95,
        durations.Max(),
        0,
        maxP95Milliseconds,
        0,
        allIterationsObservedCancellation && p95 <= maxP95Milliseconds);
}

static double Percentile(IReadOnlyList<double> values, double percentile)
{
    double[] ordered = [.. values.Order()];
    int index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
    return ordered[index];
}

static string ResolveSourceCommit()
{
    string? environmentCommit = Environment.GetEnvironmentVariable("GITHUB_SHA");
    if (!string.IsNullOrWhiteSpace(environmentCommit))
        return environmentCommit;

    return Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "unknown";
}
