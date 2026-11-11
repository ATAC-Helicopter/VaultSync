#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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

[CollectionDefinition("Product diagnostic lifecycle", DisableParallelization = true)]
public sealed class ProductDiagnosticLifecycleCollection { }

[Collection("Product diagnostic lifecycle")]
public sealed class ProductDiagnosticsFacadeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaultsync-diagnostic-facade-" + Guid.NewGuid().ToString("N"));
    private readonly ReceiptHandler handler = new();
    private readonly ProductDiagnosticReporter reporter;

    public ProductDiagnosticsFacadeTests()
    {
        reporter = new ProductDiagnosticReporter(directory, new HttpClient(handler));
        ProductDiagnostics.InitializeForTests(reporter, typeof(VaultSync.UI.App).Assembly);
    }

    [Fact]
    public async Task ApplicationFacadeSendsOnlyFutureConsentedEventsAndKeepsProductVersion()
    {
        Assert.False(ProductDiagnostics.HasDecision);
        Assert.False(ProductDiagnostics.Enabled);
        ProductDiagnostics.Record(DiagnosticOperation.Application, DiagnosticOutcome.Started);
        await reporter.FlushAsync();
        Assert.Equal(0, handler.Count);
        Assert.True(ProductDiagnostics.SetConsent(true));
        ProductDiagnostics.Record(DiagnosticOperation.UserInterface, DiagnosticOutcome.Hang,
            duration: TimeSpan.FromSeconds(4));
        await reporter.FlushAsync();
        Assert.Equal(1, handler.Count);
        using var sent = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("hang", sent.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(5000, sent.RootElement.GetProperty("durationBucketMs").GetInt32());
        Assert.Equal(typeof(VaultSync.UI.App).Assembly.GetName().Version!.ToString(),
            sent.RootElement.GetProperty("appVersion").GetString());
        Assert.True(ProductDiagnostics.SetConsent(false));
        ProductDiagnostics.Record(DiagnosticOperation.Backup, DiagnosticOutcome.Failed, new IOException("PRIVATE_SECRET"));
        await reporter.FlushAsync();
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public void NotificationSubscriberFailureCannotChangeConsentOrBreakLocalReview()
    {
        Action observer = () => throw new InvalidOperationException("PRIVATE_SECRET");
        ProductDiagnostics.ConsentChanged += observer;
        try
        {
            Assert.True(ProductDiagnostics.SetConsent(false));
            Assert.True(ProductDiagnostics.HasDecision);
            ProductDiagnostics.Record(DiagnosticOperation.Verification, DiagnosticOutcome.Failed,
                new InvalidDataException("PRIVATE_SECRET"), failure: DiagnosticFailure.IntegrityMismatch);
            string review = ProductDiagnostics.Review();
            Assert.Contains("integrityMismatch", review);
            Assert.DoesNotContain("PRIVATE_SECRET", review);
            ProductDiagnostics.DeleteReports();
            using var empty = JsonDocument.Parse(ProductDiagnostics.Review());
            Assert.Equal(0, empty.RootElement.GetArrayLength());
            Assert.True(ProductDiagnostics.HasDecision);
        }
        finally { ProductDiagnostics.ConsentChanged -= observer; }
    }

    public static IEnumerable<object[]> FailureCategories()
    {
        yield return [new OperationCanceledException("PRIVATE_SECRET"), DiagnosticFailure.Cancelled];
        yield return [new UnauthorizedAccessException("PRIVATE_SECRET"), DiagnosticFailure.AccessDenied];
        yield return [new DirectoryNotFoundException("PRIVATE_SECRET"), DiagnosticFailure.NotFound];
        yield return [new IOException("PRIVATE_SECRET", 112), DiagnosticFailure.StorageFull];
        yield return [new InvalidDataException("PRIVATE_SECRET"), DiagnosticFailure.InvalidData];
        yield return [new HttpRequestException("PRIVATE_SECRET"), DiagnosticFailure.Network];
        yield return [new TimeoutException("PRIVATE_SECRET"), DiagnosticFailure.Timeout];
        yield return [new InvalidOperationException("PRIVATE_SECRET"), DiagnosticFailure.InvalidOperation];
        yield return [new ArgumentException("PRIVATE_SECRET"), DiagnosticFailure.Argument];
        yield return [new System.Security.Cryptography.CryptographicException("PRIVATE_SECRET"), DiagnosticFailure.Cryptography];
        yield return [new OutOfMemoryException("PRIVATE_SECRET"), DiagnosticFailure.OutOfMemory];
        yield return [new Exception("PRIVATE_SECRET"), DiagnosticFailure.Other];
    }

    [Theory]
    [MemberData(nameof(FailureCategories))]
    public void ErrorCategoriesNeverRetainExceptionMessagesOrData(Exception error, DiagnosticFailure expected)
    {
        error.Data["private@example.invalid"] = "PRIVATE_SECRET";
        var report = ProductDiagnosticReport.Create(DiagnosticOperation.BackgroundTask,
            DiagnosticOutcome.Failed, error, duration: TimeSpan.FromDays(3), quantity: long.MaxValue);
        Assert.True(report.IsValid());
        Assert.Equal(expected, report.Failure);
        Assert.Equal(86400000, report.DurationBucketMs);
        Assert.Equal(int.MaxValue, report.QuantityBucket);
        string payload = JsonSerializer.Serialize(report, ProductDiagnosticReporter.JsonOptions);
        Assert.DoesNotContain("PRIVATE_SECRET", payload);
        Assert.DoesNotContain("private@example.invalid", payload);
    }

    public void Dispose()
    {
        ProductDiagnostics.Shutdown();
        reporter.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class ReceiptHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        public string? Payload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            Payload = await request.Content!.ReadAsStringAsync(token);
            using var report = JsonDocument.Parse(Payload);
            string receipt = JsonSerializer.Serialize(new { accepted = true,
                reportId = report.RootElement.GetProperty("reportId").GetString() });
            return new HttpResponseMessage(HttpStatusCode.Accepted)
                { Content = new StringContent(receipt, Encoding.UTF8, "application/json") };
        }
    }
}
