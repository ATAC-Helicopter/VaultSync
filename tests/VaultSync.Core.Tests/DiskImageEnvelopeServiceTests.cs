using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImageEnvelopeServiceTests
{
    private sealed record Fixture(string Name, string Hex, string State, string Reason);

    [Fact]
    public async Task PermanentHeadersHaveExplicitRecognitionOrRefusal()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "disk-image-envelope-v1.json");
        Fixture[] fixtures = JsonSerializer.Deserialize<Fixture[]>(await File.ReadAllTextAsync(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        foreach (Fixture fixture in fixtures)
        {
            using var source = new MemoryStream(Convert.FromHexString(fixture.Hex), writable: false);
            DiskImageEnvelopeAssessment result = await DiskImageEnvelopeService.InspectAsync(source);
            Assert.Equal(Enum.Parse<DiskImageEnvelopeState>(fixture.State), result.State);
            Assert.Equal(fixture.Reason, result.Reason);
            if (result.State == DiskImageEnvelopeState.SupportedEnvelope)
                Assert.Equal(1u, result.ManifestLength);
        }
    }

    [Fact]
    public async Task HeaderRecognitionStopsBeforeManifestOrPayload()
    {
        byte[] header = Convert.FromHexString("5653494D4147450001000000000000000100000000000000");
        using var source = new MemoryStream([.. header, 0x7f, 0x7e], writable: false);
        DiskImageEnvelopeAssessment result = await DiskImageEnvelopeService.InspectAsync(source);
        Assert.Equal(DiskImageEnvelopeState.SupportedEnvelope, result.State);
        Assert.Equal(DiskImageEnvelopeService.HeaderLength, source.Position);
    }
}
