#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

[Collection("Product diagnostic lifecycle")]
public sealed class ProductDiagnosticsBetaTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vaultsync-beta-diagnostics-" + Guid.NewGuid().ToString("N"));
    private static ProductDiagnosticReport Report(DiagnosticOutcome outcome = DiagnosticOutcome.Completed) =>
        ProductDiagnosticReport.Create(DiagnosticOperation.Backup, outcome,
            outcome == DiagnosticOutcome.Failed ? new IOException("PRIVATE /Users/person/project password=secret") : null)
        with { ReleaseChannel = "beta", ConsentVersion = 2 };
    private ProductDiagnosticReporter New(Receipt handler, bool beta = true) => new(root, new HttpClient(handler), beta);

    [Fact]
    public async Task BetaDefaultSendsOnlyNewStructuredReportsWithoutCreatingStableConsent()
    {
        var handler = new Receipt();
        using var beta = New(handler);
        Assert.True(beta.IsBeta);
        Assert.True(beta.Enabled);
        Assert.False(beta.HasDecision);
        beta.Record(Report(DiagnosticOutcome.Failed));
        await beta.FlushAsync();
        Assert.Single(handler.Bodies);
        Assert.DoesNotContain("PRIVATE", handler.Bodies[0]);
        Assert.DoesNotContain("secret", handler.Bodies[0]);
        Assert.False(File.Exists(Path.Combine(root, "consent-v1")));
        Assert.False(File.Exists(Path.Combine(root, "consent-beta-v2")));
        Assert.Equal(18, JsonDocument.Parse(handler.Bodies[0]).RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task MovingToStableNeverInheritsBetaEnablementOrItsQueue()
    {
        using (var beta = New(new Receipt()))
        {
            Assert.True(beta.SetConsent(true));
            beta.Record(Report());
            Assert.Single(Directory.GetFiles(Path.Combine(root, "pending-beta-v2")));
        }
        var handler = new Receipt();
        using var stable = New(handler, false);
        Assert.False(stable.Enabled);
        Assert.False(stable.HasDecision);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "pending-beta-v2")));
        await stable.FlushAsync();
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task BetaOptOutSurvivesRestartAndNeverBackfillsLocalHistory()
    {
        using (var beta = New(new Receipt()))
        {
            beta.Record(Report());
            Assert.True(beta.SetConsent(false));
            beta.Record(Report());
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "pending-beta-v2")));
        }
        var handler = new Receipt();
        using var restarted = New(handler);
        Assert.False(restarted.Enabled);
        Assert.True(restarted.HasDecision);
        Assert.True(restarted.SetConsent(true));
        await restarted.FlushAsync();
        Assert.Empty(handler.Bodies);
        restarted.Record(Report()); await restarted.FlushAsync();
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public void ExistingExplicitRefusalIsRespectedUntilExplicitBetaChoice()
    {
        using (var stable = New(new Receipt(), false)) Assert.True(stable.SetConsent(false));
        using (var beta = New(new Receipt()))
        {
            Assert.False(beta.Enabled); Assert.True(beta.HasDecision);
            Assert.True(beta.SetConsent(true)); Assert.True(beta.Enabled);
        }
        using var stableAgain = New(new Receipt(), false);
        Assert.False(stableAgain.Enabled);
    }

    [Theory]
    [InlineData("consent-v1", "2")]
    [InlineData("consent-v1", "")]
    [InlineData("consent-beta-v2", "2")]
    [InlineData("consent-beta-v2", "1\n")]
    public void InvalidDecisionsNeverEnableBetaDefault(string path, string bytes)
    {
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, path), bytes);
        using var beta = New(new Receipt()); Assert.False(beta.Enabled);
    }

    [Fact]
    public async Task BetaQueueRejectsEventsFromOtherBuildChannels()
    {
        var handler = new Receipt(); using var beta = New(handler);
        foreach (string channel in new[] { "development", "stable", "candidate" })
            beta.Record(Report() with { ReleaseChannel = channel });
        await beta.FlushAsync(); Assert.Empty(handler.Bodies);
        Assert.False(Directory.Exists(Path.Combine(root, "local")));
    }

    [Theory]
    [InlineData("beta", "beta")]
    [InlineData("stable", "stable")]
    [InlineData("candidate", "candidate")]
    [InlineData("development", "development")]
    [InlineData("Beta", "development")]
    [InlineData("1.9.0-beta.1", "development")]
    public void OnlyExactCompiledBetaChannelSelectsBetaPolicy(string metadata, string expected)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ReleasePolicyFixture"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!,
            new object[] { "VaultSyncReleaseChannel", metadata }));
        Assert.Equal(expected, ProductDiagnosticReport.Channel(assembly));
        var report = ProductDiagnosticReport.Create(DiagnosticOperation.Application, DiagnosticOutcome.Started, productAssembly: assembly);
        Assert.Equal(expected == "beta" ? 2 : 1, report.ConsentVersion);
        Assert.True(report.IsValid());
    }

    [Fact]
    public void BetaOrdinaryFloodCannotEvictCriticalReportsFromBoundedQueues()
    {
        using var beta = New(new Receipt());
        beta.Record(Report(DiagnosticOutcome.Crash));
        for (int i = 0; i < 110; i++) beta.Record(Report());
        using var review = JsonDocument.Parse(beta.Review());
        Assert.Equal(100, review.RootElement.GetArrayLength());
        Assert.Contains(review.RootElement.EnumerateArray(), e => e.GetProperty("outcome").GetString() == "crash");
        string[] queued = Directory.GetFiles(Path.Combine(root, "pending-beta-v2"));
        Assert.Equal(100, queued.Length);
        Assert.Contains(queued, p => File.ReadAllText(p).Contains("\"outcome\":\"crash\""));
    }

    [Fact]
    public void FullCriticalQueueRejectsOrdinaryReportsAndRetainsItsBound()
    {
        using var beta = New(new Receipt());
        for (int i = 0; i < 101; i++) beta.Record(Report(DiagnosticOutcome.Failed));
        beta.Record(Report());
        using var review = JsonDocument.Parse(beta.Review());
        Assert.Equal(100, review.RootElement.GetArrayLength());
        Assert.All(review.RootElement.EnumerateArray(), e => Assert.Equal("failed", e.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task ExpandedBoundariesRecordDurationCancellationFailureAndSuccessWithoutArguments()
    {
        using var beta = New(new Receipt()); ProductDiagnostics.InitializeForTests(beta, BetaAssembly());
        Assert.Equal(42, await ProductDiagnostics.TraceBetaAsync(DiagnosticOperation.Snapshot, () => Task.FromResult(42)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProductDiagnostics.TraceBetaAsync<int>(DiagnosticOperation.Verification,
            () => Task.FromCanceled<int>(new CancellationToken(true))));
        var failure = new IOException("PRIVATE password=secret /private/project");
        var thrown = await Assert.ThrowsAsync<IOException>(() => ProductDiagnostics.TraceBetaAsync<int>(DiagnosticOperation.Backup,
            () => Task.FromException<int>(failure)));
        Assert.Same(failure, thrown);
        await ProductDiagnostics.TraceBetaAsync(DiagnosticOperation.Restore, () => Task.CompletedTask);
        using var review = JsonDocument.Parse(beta.Review());
        Assert.Equal(8, review.RootElement.GetArrayLength());
        Assert.Contains(review.RootElement.EnumerateArray(), e => e.GetProperty("outcome").GetString() == "cancelled");
        Assert.Contains(review.RootElement.EnumerateArray(), e => e.GetProperty("failure").GetString() == "io");
        Assert.DoesNotContain("PRIVATE", beta.Review()); Assert.DoesNotContain("secret", beta.Review());
    }

    [Fact]
    public async Task OtherChannelsDoNotGainExpandedTracing()
    {
        using var stable = New(new Receipt(), false); ProductDiagnostics.InitializeForTests(stable);
        Assert.Equal(42, await ProductDiagnostics.TraceBetaAsync(DiagnosticOperation.Snapshot, () => Task.FromResult(42)));
        Assert.Equal("[]", stable.Review());
    }

    [Fact]
    public async Task RealOperationEntryPointsCaptureEarlyFailuresWithoutSerializingArguments()
    {
        using var beta = New(new Receipt()); ProductDiagnostics.InitializeForTests(beta, BetaAssembly());
        var repository = VaultSync.Core.Tests.TestSupport.TestRepository.Create(Path.Combine(root, "test.db"));
        var project = new Project { Id = 999, Name = "PRIVATE_PROJECT", Preset = "dotnet", RootPath = "" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BackupService(repository).RunBackupAsync(
            new BackupService.BackupRunRequest { Project = project, BackupRoot = "PRIVATE_DESTINATION" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SnapshotService(repository, new HashService())
            .CreateSnapshotAsync(project, false, new SnapshotCreationOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VerifyService(repository, new HashService())
            .VerifyAsync(project, "PRIVATE_DESTINATION", 100, true));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => VaultSync.UI.Services.RestoreExecutionService.RestoreAsync(
            Path.Combine(root, "PRIVATE_MISSING_SOURCE"), Path.Combine(root, "PRIVATE_TARGET"), "PRIVATE_PASSWORD", null, null, default));
        using var review = JsonDocument.Parse(beta.Review());
        Assert.Equal(8, review.RootElement.GetArrayLength());
        Assert.Equal(4, review.RootElement.EnumerateArray().Count(e => e.GetProperty("outcome").GetString() == "failed"));
        Assert.DoesNotContain("PRIVATE", beta.Review());
    }

    [Fact]
    public async Task RealRestoreSuccessRecordsOutcomeWithoutFileNameOrContents()
    {
        using var beta = New(new Receipt()); ProductDiagnostics.InitializeForTests(beta, BetaAssembly());
        string source = Path.Combine(root, "PRIVATE_SOURCE"), target = Path.Combine(root, "PRIVATE_TARGET");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "PRIVATE_FILE"), "PRIVATE_CONTENTS");
        await VaultSync.UI.Services.RestoreExecutionService.RestoreAsync(source, target, null, null, null, default);
        Assert.Equal("PRIVATE_CONTENTS", File.ReadAllText(Path.Combine(target, "PRIVATE_FILE")));
        using var review = JsonDocument.Parse(beta.Review());
        Assert.Equal(2, review.RootElement.GetArrayLength());
        Assert.Contains(review.RootElement.EnumerateArray(), e => e.GetProperty("outcome").GetString() == "completed");
        Assert.DoesNotContain("PRIVATE", beta.Review());
    }

    [Fact]
    public void FailedBetaOptOutPersistenceDisablesImmediatelyAndInvalidChoiceFailsClosed()
    {
        using (var beta = New(new Receipt()))
        {
            beta.Record(Report());
            Directory.CreateDirectory(Path.Combine(root, "consent-beta-v2"));
            Assert.False(beta.SetConsent(false));
            Assert.False(beta.Enabled);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "pending-beta-v2")));
        }
        using var restarted = New(new Receipt());
        Assert.False(restarted.Enabled);
    }

    [Fact]
    public async Task VerificationMismatchNeverProducesASuccessfulTerminalReport()
    {
        using var beta = New(new Receipt()); ProductDiagnostics.InitializeForTests(beta, BetaAssembly());
        string destination = Path.Combine(root, "PRIVATE_DESTINATION"); Directory.CreateDirectory(destination);
        var repository = VaultSync.Core.Tests.TestSupport.TestRepository.Create(Path.Combine(root, "test.db"));
        int id = VaultSync.Core.Tests.TestSupport.TestRepository.AddProject(repository, "PRIVATE_PROJECT", root);
        var project = new Project { Id = id, Name = "PRIVATE_PROJECT", Preset = "dotnet", RootPath = root };
        int snapshot = repository.CreateSnapshot(id, DateTime.UtcNow.Ticks, 20);
        repository.InsertFiles(snapshot, new[] { new FileEntry("PRIVATE_MISSING_FILE", 20, DateTime.UtcNow, "EXPECTED") });
        var result = await new VerifyService(repository, new HashService()).VerifyAsync(project, destination, 100, true);
        Assert.Single(result.Failures);
        using var review = JsonDocument.Parse(beta.Review());
        Assert.Equal(2, review.RootElement.GetArrayLength());
        Assert.Contains(review.RootElement.EnumerateArray(), e => e.GetProperty("outcome").GetString() == "failed");
        Assert.DoesNotContain(review.RootElement.EnumerateArray(), e => e.GetProperty("outcome").GetString() == "completed");
        Assert.DoesNotContain("PRIVATE", beta.Review());
    }

    [Fact]
    public async Task ExplicitStableOptInStillRejectsCopiedBetaDefaultQueueRecords()
    {
        var handler = new Receipt(); using var stable = New(handler, false);
        Assert.True(stable.SetConsent(true));
        var report = Report();
        stable.Record(report);
        string pending = Path.Combine(root, "pending"); Directory.CreateDirectory(pending);
        File.WriteAllText(Path.Combine(pending, report.ReportId.ToString("D") + ".json"),
            JsonSerializer.Serialize(report, ProductDiagnosticReporter.JsonOptions));
        await stable.FlushAsync();
        Assert.Empty(handler.Bodies); Assert.Empty(Directory.GetFiles(pending));
        stable.Record(ProductDiagnosticReport.Create(DiagnosticOperation.Application, DiagnosticOutcome.Started));
        await stable.FlushAsync(); Assert.Single(handler.Bodies);
    }

    private static Assembly BetaAssembly()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("BetaBuildFixture"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!,
            new object[] { "VaultSyncReleaseChannel", "beta" }));
        return assembly;
    }
    public void Dispose() { ProductDiagnostics.Shutdown(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class Receipt : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken); Bodies.Add(body);
            using var doc = JsonDocument.Parse(body);
            return new(HttpStatusCode.Accepted) { Content = new StringContent(JsonSerializer.Serialize(new
                { accepted = true, reportId = doc.RootElement.GetProperty("reportId").GetGuid() })) };
        }
    }
}
