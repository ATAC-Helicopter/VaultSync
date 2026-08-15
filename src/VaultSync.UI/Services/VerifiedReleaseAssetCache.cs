using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VaultSync.Core.Services;

namespace VaultSync.UI.Services
{
    /// <summary>
    /// Persists immutable release metadata by trusted URL, size, and SHA-256 identity.
    /// Cached bytes are revalidated before every use so disk contents never become a
    /// substitute for the digest published by GitHub.
    /// </summary>
    internal sealed class VerifiedReleaseAssetCache
    {
        private static readonly ConcurrentDictionary<string, object> s_pathGates =
            new(StringComparer.Ordinal);

        internal static VerifiedReleaseAssetCache Default { get; } =
            new(ResolveDefaultCacheDirectory());

        private readonly string _cacheDirectory;

        internal VerifiedReleaseAssetCache(string cacheDirectory)
        {
            _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
                ? string.Empty
                : Path.GetFullPath(cacheDirectory);
        }

        internal bool TryRead(
            string assetUrl,
            string expectedSha256,
            long expectedSize,
            long maximumSize,
            out byte[] payload)
        {
            payload = [];
            if (!TryGetCachePath(assetUrl, expectedSha256, expectedSize, maximumSize, out string cachePath))
                return false;

            try
            {
                if (!File.Exists(cachePath) || IsLinkedFile(cachePath))
                    return false;

                using var stream = new FileStream(
                    cachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);
                if (stream.Length != expectedSize)
                    return false;

                var candidate = new byte[(int)expectedSize];
                stream.ReadExactly(candidate);
                if (stream.ReadByte() != -1)
                    return false;

                if (!IsExpectedPayload(candidate, expectedSha256, expectedSize, maximumSize))
                    return false;

                payload = candidate;
                return true;
            }
            catch (Exception ex) when (IsCacheException(ex))
            {
                return false;
            }
        }

        internal void Write(
            string assetUrl,
            string expectedSha256,
            long expectedSize,
            long maximumSize,
            byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (!IsExpectedPayload(payload, expectedSha256, expectedSize, maximumSize) ||
                !TryGetCachePath(assetUrl, expectedSha256, expectedSize, maximumSize, out string cachePath))
            {
                return;
            }

            object pathGate = s_pathGates.GetOrAdd(cachePath, static _ => new object());
            lock (pathGate)
            {
                string temporaryPath = Path.Combine(
                    _cacheDirectory,
                    $".{Path.GetFileName(cachePath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    PrivateDataPermissions.EnsureDirectory(_cacheDirectory);
                    if (TryRead(assetUrl, expectedSha256, expectedSize, maximumSize, out _))
                        return;
                    if (File.Exists(cachePath) && IsLinkedFile(cachePath))
                        return;

                    using (var stream = new FileStream(
                               temporaryPath,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               bufferSize: 4096,
                               FileOptions.WriteThrough))
                    {
                        stream.Write(payload);
                        stream.Flush(flushToDisk: true);
                    }

                    PrivateDataPermissions.RestrictFile(temporaryPath);
                    File.Move(temporaryPath, cachePath, overwrite: true);
                    PrivateDataPermissions.RestrictFile(cachePath);
                }
                catch (Exception ex) when (IsCacheException(ex))
                {
                    // Caching is an optimization. A cache failure must not block update checks.
                }
                finally
                {
                    TryDeleteTemporaryFile(temporaryPath);
                }
            }
        }

        private bool TryGetCachePath(
            string assetUrl,
            string expectedSha256,
            long expectedSize,
            long maximumSize,
            out string cachePath)
        {
            cachePath = string.Empty;
            if (string.IsNullOrWhiteSpace(_cacheDirectory) ||
                !Uri.TryCreate(assetUrl, UriKind.Absolute, out _) ||
                !IsValidIdentity(expectedSha256, expectedSize, maximumSize))
            {
                return false;
            }

            string identity = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{assetUrl}|{expectedSha256.ToLowerInvariant()}|{expectedSize}");
            string fileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant() + ".json";
            cachePath = Path.Combine(_cacheDirectory, fileName);
            return true;
        }

        private static bool IsExpectedPayload(
            byte[] payload,
            string expectedSha256,
            long expectedSize,
            long maximumSize)
        {
            if (!IsValidIdentity(expectedSha256, expectedSize, maximumSize) ||
                payload.LongLength != expectedSize)
            {
                return false;
            }

            byte[] expectedHash = Convert.FromHexString(expectedSha256);
            byte[] actualHash = SHA256.HashData(payload);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }

        private static bool IsValidIdentity(string expectedSha256, long expectedSize, long maximumSize) =>
            expectedSize > 0 &&
            expectedSize <= maximumSize &&
            expectedSize <= int.MaxValue &&
            maximumSize > 0 &&
            !string.IsNullOrWhiteSpace(expectedSha256) &&
            expectedSha256.Length == 64 &&
            expectedSha256.All(Uri.IsHexDigit);

        private static bool IsLinkedFile(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (IsCacheException(ex))
            {
                return true;
            }
        }

        private static void TryDeleteTemporaryFile(string path)
        {
            try
            {
                if (File.Exists(path) && !IsLinkedFile(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (IsCacheException(ex))
            {
            }
        }

        private static bool IsCacheException(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException;

        private static string ResolveDefaultCacheDirectory()
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localData)
                ? string.Empty
                : Path.Combine(localData, "VaultSync", "cache", "release-assets");
        }
    }
}
