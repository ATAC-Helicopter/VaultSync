#nullable enable
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;

namespace VaultSync.Core.Tests;

// Synthetic in-process unit provider ONLY, never product fallback or native proof.
internal static class DiskImageInspectionFixture
{
    internal static async Task<DiskImageInspectionWorkerResult> InspectAsync(Stream metadata, Stream payload, CancellationToken token)
    {
        metadata.Position = 0; payload.Position = 0;
        return new(DiskImageInspectionWorkerOutcome.Completed,
            await DiskImageInspectionWorkerService.VerifyImageAsync(metadata, payload, token));
    }
}
