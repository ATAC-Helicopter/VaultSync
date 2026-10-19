using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Bounded local technical events and a separately consented queue; never uploads existing logs.</summary>
public sealed class ProductDiagnosticReporter : IDisposable
{
    public const string Endpoint = "https://fglabs.dev/api/vaultsync/diagnostics";
    public const int MaximumReportBytes = 8192;
    public const int MaximumReports = 100;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private readonly string local;
    private readonly string pending;
    private readonly string decisionPath;
    private readonly HttpClient http;
    private readonly object gate = new();
    private readonly SemaphoreSlim flushGate = new(1, 1);
    private CancellationTokenSource consentLifetime = new();
    private bool consent;
    private bool disposed;
    private bool hasDecision;
    private int failures;
    private DateTimeOffset nextAttempt;

    public ProductDiagnosticReporter(string directory) : this(directory, new HttpClient(new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) }) { }

    internal ProductDiagnosticReporter(string directory, HttpClient client)
    {
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException();
        PrivateDataPermissions.EnsureDirectory(directory);
        local = Path.Combine(Path.GetFullPath(directory), "local");
        pending = Path.Combine(Path.GetFullPath(directory), "pending");
        decisionPath = Path.Combine(Path.GetFullPath(directory), "consent-v1");
        http = client;
        try
        {
            if (File.Exists(decisionPath) && (File.GetAttributes(decisionPath) & FileAttributes.ReparsePoint) == 0)
            {
                using var file = File.OpenRead(decisionPath);
                int decision = file.ReadByte();
                if (decision is (byte)'0' or (byte)'1' && file.ReadByte() == -1)
                { consent = decision == (byte)'1'; hasDecision = true; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!consent) DeleteManaged(pending);
        Prune(local);
        Prune(pending);
    }

    public bool Enabled { get { lock (gate) return consent; } }
    public bool HasDecision { get { lock (gate) return hasDecision; } }

    // Consent is a version-1 device-local decision, deliberately outside exported/synced app configuration.
    public bool SetConsent(bool enabled)
    {
        lock (gate)
        {
            if (disposed) return false;
            if (!enabled)
            {
                consent = false;
                consentLifetime.Cancel();
                DeleteManaged(pending);
            }
            string temporary = decisionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Do not follow a pre-existing temporary symlink or truncate another file.
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(temporary, options)) { file.WriteByte(enabled ? (byte)'1' : (byte)'0'); file.Flush(true); }
                File.Move(temporary, decisionPath, overwrite: true);
                hasDecision = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                consent = false;
                consentLifetime.Cancel();
                DeleteManaged(pending);
                return false;
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (enabled && !consent)
            {
                consentLifetime.Dispose();
                consentLifetime = new();
                consent = true;
                failures = 0;
                nextAttempt = default;
            }
            return true;
        }
    }

    public void Record(ProductDiagnosticReport report)
    {
        try
        {
            if (!report.IsValid()) return;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            if (bytes.Length > MaximumReportBytes) return;
            lock (gate)
            {
                if (disposed) return;
                Write(local, report.ReportId, bytes);
                if (consent) Write(pending, report.ReportId, bytes);
            }
        }
        catch { /* Diagnostics must never interrupt protection operations. */ }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!await flushGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            // A batch is capped independently of queue length and network availability.
            for (int index = 0; index < 10; index++)
            {
                string path;
                Guid reportId;
                Task<HttpResponseMessage> request;
                CancellationTokenSource lifetime;
                lock (gate)
                {
                    if (disposed) return;
                    Prune(local);
                    if (!consent || DateTimeOffset.UtcNow < nextAttempt) return;
                    Prune(pending);
                    path = Files(pending).FirstOrDefault() ?? "";
                    if (path.Length == 0) return;
                    ProductDiagnosticReport? report = Read(path);
                    if (report is null) { File.Delete(path); continue; }
                    reportId = report.ReportId;
                    lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, consentLifetime.Token);
                    lifetime.CancelAfter(TimeSpan.FromSeconds(10)); // Includes reading the bounded acknowledgement body.
                    var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                    { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions)) };
                    message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    request = SendAndDisposeAsync(message, lifetime.Token);
                }
                using (lifetime)
                {
                    try
                    {
                        using HttpResponseMessage response = await request.ConfigureAwait(false);
                        bool acknowledged = response.StatusCode == HttpStatusCode.Accepted &&
                            await Acknowledged(response, reportId, lifetime.Token).ConfigureAwait(false);
                        lock (gate)
                        {
                            if (acknowledged || response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType)
                            { File.Delete(path); failures = 0; }
                            else { Backoff(response.Headers.RetryAfter?.Delta); return; }
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
                    {
                        lock (gate) { Backoff(); }
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        finally { flushGate.Release(); }
    }

    private async Task<HttpResponseMessage> SendAndDisposeAsync(HttpRequestMessage request, CancellationToken token)
    {
        using (request) return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    private static async Task<bool> Acknowledged(HttpResponseMessage response, Guid id, CancellationToken token)
    {
        using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] bytes = new byte[257];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > 256) return false;
        using JsonDocument doc = JsonDocument.Parse(bytes.AsMemory(0, count));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
        var properties = doc.RootElement.EnumerateObject().ToArray();
        return properties.Length == 2 && properties.Select(p => p.Name).Distinct().Count() == 2 &&
            doc.RootElement.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.True &&
            doc.RootElement.TryGetProperty("reportId", out var reportId) && reportId.ValueKind == JsonValueKind.String && reportId.TryGetGuid(out Guid actual) && actual == id;
    }

    private void Backoff(TimeSpan? retryAfter = null)
    {
        failures = Math.Min(failures + 1, 10);
        nextAttempt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(21600,
            Math.Max(30 * Math.Pow(2, failures), retryAfter?.TotalSeconds ?? 0)));
    }

    private static ProductDiagnosticReport? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] bytes = new byte[MaximumReportBytes + 1];
            int count = 0;
            while (count < bytes.Length)
            {
                int n = file.Read(bytes, count, bytes.Length - count);
                if (n == 0) break;
                count += n;
            }
            if (count > MaximumReportBytes) return null;
            using JsonDocument doc = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            if (Duplicates(doc.RootElement)) return null;
            ProductDiagnosticReport? report = JsonSerializer.Deserialize<ProductDiagnosticReport>(bytes.AsSpan(0, count), JsonOptions);
            return report?.IsValid() == true && Path.GetFileNameWithoutExtension(path) == report.ReportId.ToString("D") ? report : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    private static bool Duplicates(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != element.EnumerateObject().Count() ||
            element.EnumerateObject().Any(p => Duplicates(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(Duplicates),
        _ => false
    };

    private static string[] Files(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException();
        string[] files = Directory.EnumerateFiles(directory, "*.json").Take(201).ToArray();
        if (files.Length > 200) throw new IOException();
        return files.Where(p => Guid.TryParseExact(Path.GetFileNameWithoutExtension(p), "D", out _) &&
            (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).OrderBy(File.GetLastWriteTimeUtc).ToArray();
    }

    private static void Write(string directory, Guid id, byte[] bytes)
    {
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
        PrivateDataPermissions.EnsureDirectory(directory);
        Prune(directory, MaximumReports - 1);
        string path = Path.Combine(directory, id.ToString("D") + ".json");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
        file.Write(bytes);
    }

    private static void Prune(string directory, int maximum = MaximumReports)
    {
        string[] files = Files(directory);
        foreach (string path in files.Take(Math.Max(0, files.Length - maximum))
                     .Concat(files.Where(p => File.GetLastWriteTimeUtc(p) < DateTime.UtcNow.AddDays(-7))).Distinct())
            File.Delete(path);
    }

    private static void DeleteManaged(string directory)
    {
        try { foreach (string path in Files(directory)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public string Review()
    {
        lock (gate)
        {
            Prune(local);
            return JsonSerializer.Serialize(Files(local).Select(Read).OfType<ProductDiagnosticReport>().ToArray(),
                new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
        }
    }

    public void DeleteReports() { lock (gate) { DeleteManaged(local); DeleteManaged(pending); } }
    public void Dispose()
    {
        lock (gate) { disposed = true; consent = false; consentLifetime.Cancel(); }
        http.Dispose();
    }
}
