using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using VaultSync.Core.Services;

namespace VaultSync.UI.Services;

internal static class InstallerIntegrityVerifier
{
    internal const long MaxInstallerBytes = 2L * 1024 * 1024 * 1024;

    public static bool Verify(string filePath, long expectedSize, string expectedSha256)
    {
        if (expectedSize <= 0 || expectedSize > MaxInstallerBytes ||
            expectedSha256 is not { Length: 64 } ||
            !expectedSha256.All(Uri.IsHexDigit))
        {
            return false;
        }

        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length != expectedSize)
            return false;

        using FileStream stream = info.OpenRead();
        string actual = HashService.FormatSha256Lower(SHA256.HashData(stream));
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual),
            Convert.FromHexString(expectedSha256));
    }
}
