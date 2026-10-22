#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProductDiagnosticReporterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vaultsync-private-diagnostics-" + Guid.NewGuid().ToString("N"));
    private static ProductDiagnosticReport Report => ProductDiagnosticReport.Create(DiagnosticOperation.Backup, DiagnosticOutcome.Failed,
        new IOException("/Users/PRIVATE_PERSON/private-project/secret.txt password=TOP_SECRET mail=private@example.test"), DiagnosticPhase.Write,
        DiagnosticMode.Zip, TimeSpan.FromSeconds(7), 42);

    [Fact]
    public void TimeOrHardwareBasedUuidsAreNotAcceptedAsReportIdentifiers()
    {
        Assert.False((Report with { ReportId = Guid.Parse("00112233-4455-1677-8899-aabbccddeeff") }).IsValid());
    }

    [Fact]
    public void ProductVersionUsesTheApplicationAssemblyInsteadOfTheLibraryDefault()
    {
        var report = ProductDiagnosticReport.Create(DiagnosticOperation.Application, DiagnosticOutcome.Started,
            productAssembly: typeof(VaultSync.UI.App).Assembly);
        Assert.StartsWith("1.9.0", report.AppVersion);
        Assert.Equal("development", report.ReleaseChannel);
        Assert.True(report.IsValid());
    }

    [Fact]
    public void CollectorNeverReadsExceptionMessagesPathsOrCustomTypeNames()
    {
        ProductDiagnosticReport report = ProductDiagnosticReport.Create(DiagnosticOperation.Application, DiagnosticOutcome.Crash,
            new PersonalNamedException("SECRET /home/PRIVATE_PERSON/data"));
        string json = JsonSerializer.Serialize(report, ProductDiagnosticReporter.JsonOptions);
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("PRIVATE_PERSON", json);
        Assert.DoesNotContain("PersonalNamed", json);
        Assert.Equal(DiagnosticFailure.Other, report.Failure);
        Assert.Equal(1, report.SchemaVersion);
        Assert.NotEqual(Report.ReportId, report.ReportId);
    }

    [Fact]
    public async Task DefaultOnlyWritesLocallyAndDoesNotUploadHistoryAfterOptIn()
    {
        var transport = new Transport();
        using var reporter = New(transport);
        Assert.False(reporter.Enabled);
        Assert.False(reporter.HasDecision);
        reporter.Record(Report);
        await reporter.FlushAsync();
        Assert.Empty(transport.Bodies);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "local")));
        Assert.True(reporter.SetConsent(true));
        await reporter.FlushAsync();
        Assert.Empty(transport.Bodies);
        reporter.Record(Report);
        await reporter.FlushAsync();
        Assert.Single(transport.Bodies);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "pending")));
        Assert.DoesNotContain("TOP_SECRET", transport.Bodies[0]);
    }

    [Fact]
    public async Task ConsentIsLocalAndSurvivesRestartButMissingDecisionPurgesQueue()
    {
        using (var reporter = New(new Transport())) { Assert.True(reporter.SetConsent(true)); reporter.Record(Report); }
        var transport = new Transport();
        using (var reporter = New(transport)) { Assert.True(reporter.Enabled); Assert.True(reporter.HasDecision); await reporter.FlushAsync(); }
        Assert.Single(transport.Bodies);
        using (var reporter = New(new Transport())) reporter.Record(Report);
        File.Delete(Path.Combine(root, "consent-v1"));
        using (var reporter = New(new Transport())) { Assert.False(reporter.Enabled); Assert.Empty(Directory.GetFiles(Path.Combine(root, "pending"))); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("1\n")]
    [InlineData("11")]
    [InlineData("2")]
    public void InvalidOrFutureConsentFailsClosed(string content)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "consent-v1"), content);
        using var reporter = New(new Transport());
        Assert.False(reporter.Enabled);
        Assert.False(reporter.HasDecision);
    }

    [Fact]
    public async Task RevocationCancelsInFlightAndPurgesWithoutDeletingReviewedLocalReports()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport(async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
        });
        using var reporter = New(transport);
        Assert.True(reporter.SetConsent(true));
        reporter.Record(Report);
        Task sending = reporter.FlushAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reporter.SetConsent(false));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sending;
        Assert.False(reporter.Enabled);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "pending")));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "local")));
        reporter.Record(Report);
        await reporter.FlushAsync();
        Assert.Single(transport.Bodies);
        using var restarted = New(new Transport());
        Assert.False(restarted.Enabled);
        Assert.True(restarted.HasDecision);
    }

    [Fact]
    public async Task QueueIsBoundedAndAcknowledgementCorrelatesReportId()
    {
        var transport = new Transport();
        using var reporter = New(transport);
        reporter.SetConsent(true);
        for (int i = 0; i < 120; i++) reporter.Record(Report);
        Assert.Equal(100, Directory.GetFiles(Path.Combine(root, "local")).Length);
        Assert.Equal(100, Directory.GetFiles(Path.Combine(root, "pending")).Length);
        await reporter.FlushAsync();
        Assert.Equal(10, transport.Bodies.Count); // Per-pass limit.
        Assert.Equal(90, Directory.GetFiles(Path.Combine(root, "pending")).Length);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task RetryableFailuresBackOffWithoutLosingReports(int status)
    {
        var transport = new Transport { Status = (HttpStatusCode)status };
        using var reporter = New(transport);
        reporter.SetConsent(true); reporter.Record(Report);
        await reporter.FlushAsync(); await reporter.FlushAsync();
        Assert.Single(transport.Bodies);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "pending")));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [InlineData(415)]
    public async Task PermanentlyInvalidReportIsDropped(int status)
    {
        using var reporter = New(new Transport { Status = (HttpStatusCode)status });
        reporter.SetConsent(true); reporter.Record(Report);
        await reporter.FlushAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "pending")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"accepted\":true,\"reportId\":2}")]
    [InlineData("{\"accepted\":true,\"reportId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"accepted\":false,\"reportId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task BadAcknowledgementDoesNotClaimSuccessfulDelivery(string ack)
    {
        using var reporter = New(new Transport { Acknowledgement = ack });
        reporter.SetConsent(true); reporter.Record(Report);
        await reporter.FlushAsync();
        Assert.Single(Directory.GetFiles(Path.Combine(root, "pending")));
    }

    [Fact]
    public async Task MaliciousQueueCannotUploadAdditionalFieldsOrPersonalStrings()
    {
        var transport = new Transport(); using var reporter = New(transport);
        reporter.SetConsent(true);
        var report = Report; reporter.Record(report);
        string path = Path.Combine(root, "pending", report.ReportId + ".json");
        string original = File.ReadAllText(path);
        string[] malicious = [original.Replace("\"appVersion\":", "\"privatePath\":\"SECRET\",\"appVersion\":"),
            original.Replace("\"backup\"", "\"PRIVATE_PERSON\""), original.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            original.Replace("\"codeSites\":[]", "\"codeSites\":[{\"module\":\"core\",\"token\":1}]"), new string('x', 8193)];
        foreach (string json in malicious)
        {
            File.WriteAllText(path, json);
            await reporter.FlushAsync();
            Assert.Empty(transport.Bodies);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public async Task ConcurrentFlushDoesNotDoubleSend()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reporter = New(new Transport(async _ => { started.SetResult(); await release.Task; }));
        reporter.SetConsent(true); reporter.Record(Report);
        Task flush = reporter.FlushAsync(); await started.Task;
        await reporter.FlushAsync(); release.SetResult(); await flush;
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "pending")));
    }

    [Fact]
    public async Task ExpiredLocalAndQueuedReportsAreDeletedEvenWithoutSending()
    {
        using var reporter = New(new Transport()); reporter.SetConsent(true); var report = Report; reporter.Record(report);
        foreach (string directory in new[] { "local", "pending" }) File.SetLastWriteTimeUtc(Path.Combine(root, directory, report.ReportId + ".json"), DateTime.UtcNow.AddDays(-8));
        await reporter.FlushAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "local")));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "pending")));
    }

    [Fact]
    public void ReviewAndDeleteExposeOnlyAllowlistedTechnicalFields()
    {
        using var reporter = New(new Transport()); reporter.SetConsent(true); reporter.Record(Report);
        string review = reporter.Review();
        Assert.DoesNotContain("PRIVATE_PERSON", review);
        Assert.DoesNotContain("private-project", review);
        Assert.Contains("durationBucketMs", review);
        reporter.DeleteReports(); Assert.Equal("[]", reporter.Review());
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "pending")));
    }

    [Fact]
    public void SymlinkedQueueIsNotReadOrDeleted()
    {
        if (OperatingSystem.IsWindows()) return;
        using var reporter = New(new Transport());
        string outside = root + "-outside"; Directory.CreateDirectory(outside);
        try
        {
            string victim = Path.Combine(outside, Guid.NewGuid() + ".json"); File.WriteAllText(victim, "PRIVATE_PERSON");
            Directory.CreateSymbolicLink(Path.Combine(root, "pending"), outside);
            reporter.SetConsent(false);
            Assert.Equal("PRIVATE_PERSON", File.ReadAllText(victim));
        }
        finally { Directory.Delete(Path.Combine(root, "pending")); Directory.Delete(outside, true); }
    }

    [Fact]
    public void StorageFailuresCannotBreakTheApplicationOrSilentlyEnableSending()
    {
        using var reporter = New(new Transport());
        Directory.CreateDirectory(Path.Combine(root, "consent-v1"));
        Assert.False(reporter.SetConsent(true));
        Assert.False(reporter.Enabled);
        File.WriteAllText(Path.Combine(root, "local"), "not a report directory");
        reporter.Record(Report); // Must not throw.
    }

    [Fact]
    public async Task AcknowledgementBodyCannotKeepTheRequestOpenIndefinitely()
    {
        using var reporter = new ProductDiagnosticReporter(root, new HttpClient(new HangingAcknowledgement()));
        reporter.SetConsent(true); reporter.Record(Report);
        await reporter.FlushAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "pending")));
    }

    private sealed class HangingAcknowledgement : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StreamContent(new HangingBody()) });
    }
    private sealed class HangingBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
    }

    private ProductDiagnosticReporter New(Transport transport) => new(root, new HttpClient(transport));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class PersonalNamedException(string message) : Exception(message);
    private sealed class Transport(Func<CancellationToken, Task>? wait = null) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.Accepted;
        public string? Acknowledgement { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(ProductDiagnosticReporter.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Empty(request.Headers);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken); Bodies.Add(body);
            if (wait is not null) await wait(cancellationToken);
            var report = JsonSerializer.Deserialize<ProductDiagnosticReport>(body, ProductDiagnosticReporter.JsonOptions)!;
            return new HttpResponseMessage(Status) { Content = new StringContent(Acknowledgement ?? JsonSerializer.Serialize(new { accepted = true, reportId = report.ReportId }), Encoding.UTF8, "application/json") };
        }
    }
}
