#nullable enable
using System.IO;
using System.Security.Cryptography;
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
        var verified = await DiskImageRawProfileVerificationService.VerifyAsync(metadata, payload, token);
        string? hash = verified.State == DiskImageRawProfileVerificationState.ContentAndLayoutVerified
            ? await DiskImageByteIntegrity.HashExactAsync(payload, (ulong)payload.Length, token) : null;
        return new(DiskImageInspectionWorkerOutcome.Completed, DiskImageInspectionProtocol.Observe(verified, hash));
    }
}
