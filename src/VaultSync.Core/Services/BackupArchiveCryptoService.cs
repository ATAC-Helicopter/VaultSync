using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VaultSync.Core.Config;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class BackupArchiveCryptoService
{
    public const string PlainArchiveFileName = "data.zip";
    public const string EncryptedArchiveFileName = "data.vse";
    public const string MetadataFileName = ".vaultsync_crypto.json";
    public const string EnvelopeMagic = "VSENC1";
    public const int CurrentEnvelopeVersion = 1;
    public const string InvalidPasswordOrCorruptedMessage =
        "Invalid backup password or corrupted encrypted backup archive.";

    private const int DefaultKdfIterations = 210_000;
    private const int SaltLengthBytes = 16;
    private const int IvLengthBytes = 16;
    private const int DerivedKeyLengthBytes = 64;
    private const int EncryptionKeyLengthBytes = 32;
    private const int HmacKeyLengthBytes = 32;
    private const int HmacLengthBytes = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public sealed record EncryptionResult(
        bool IsEncrypted,
        string EncryptedArchivePath,
        BackupCryptoDescriptor Descriptor,
        long EncryptedBytes);

    public static EncryptionResult EncryptArchiveInPlace(
        string backupFolder,
        string password,
        BackupEncryptionConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(backupFolder))
            throw new ArgumentException("Backup folder is required.", nameof(backupFolder));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Backup encryption password is required.", nameof(password));

        string plainArchivePath = Path.Combine(backupFolder, PlainArchiveFileName);
        if (!File.Exists(plainArchivePath))
            throw new FileNotFoundException("Archive backup artifact not found.", plainArchivePath);

        string algorithm = ResolveAlgorithm(config.Algorithm);
        string kdfProfile = ResolveKdfProfile(config.KdfProfile);
        int iterations = ResolveKdfIterations(config.KdfParamRef);
        string kdfParamRef = ResolveKdfParamRef(config.KdfParamRef, iterations);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLengthBytes);
        byte[] iv = RandomNumberGenerator.GetBytes(IvLengthBytes);
        BackupArchiveEnvelopeMetadata metadata = BuildMetadata(algorithm, kdfProfile, kdfParamRef, iterations, salt, iv);
        var descriptor = BackupCryptoDescriptor.Encrypted(
            metadata.Algorithm,
            metadata.KdfProfile,
            metadata.KdfParamRef,
            formatVersion: metadata.FormatVersion);

        string encryptedArchivePath = Path.Combine(backupFolder, EncryptedArchiveFileName);
        try
        {
            WriteEncryptedArchive(plainArchivePath, encryptedArchivePath, password, metadata, salt, iv, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(iv);
        }

        string metadataPath = Path.Combine(backupFolder, MetadataFileName);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions), Encoding.UTF8);

        File.Delete(plainArchivePath);

        long encryptedBytes = new FileInfo(encryptedArchivePath).Length;
        return new EncryptionResult(true, encryptedArchivePath, descriptor, encryptedBytes);
    }

    public static bool TryReadDescriptor(string backupFolder, out BackupCryptoDescriptor descriptor, out bool isEncrypted)
    {
        descriptor = BackupCryptoDescriptor.Plain();
        isEncrypted = false;

        if (string.IsNullOrWhiteSpace(backupFolder))
            return false;

        try
        {
            string metadataPath = Path.Combine(backupFolder, MetadataFileName);
            if (File.Exists(metadataPath))
            {
                string json = File.ReadAllText(metadataPath);
                BackupArchiveEnvelopeMetadata? metadata = JsonSerializer.Deserialize<BackupArchiveEnvelopeMetadata>(json, JsonOptions);
                if (metadata is not null)
                {
                    descriptor = BackupCryptoDescriptor.Encrypted(
                        metadata.Algorithm,
                        metadata.KdfProfile,
                        metadata.KdfParamRef,
                        metadata.FormatVersion);
                    isEncrypted = true;
                    return true;
                }
            }

            string encryptedArchivePath = Path.Combine(backupFolder, EncryptedArchiveFileName);
            if (File.Exists(encryptedArchivePath))
            {
                descriptor = BackupCryptoDescriptor.Encrypted("unknown", "unknown", string.Empty);
                isEncrypted = true;
                return true;
            }
        }
        catch
        {
            // Ignore malformed metadata and fall back to plain backup contract.
        }

        return false;
    }

    public static long GetStoredArchiveSize(string backupFolder)
    {
        try
        {
            string encryptedPath = Path.Combine(backupFolder, EncryptedArchiveFileName);
            if (File.Exists(encryptedPath))
                return new FileInfo(encryptedPath).Length;

            string plainPath = Path.Combine(backupFolder, PlainArchiveFileName);
            if (File.Exists(plainPath))
                return new FileInfo(plainPath).Length;
        }
        catch
        {
            // Ignore size probing failures and report unknown size.
        }

        return 0;
    }

    public static void DecryptArchiveToPlainZip(
        string backupFolder,
        string password,
        string outputArchivePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(backupFolder))
            throw new ArgumentException("Backup folder is required.", nameof(backupFolder));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Backup encryption password is required.", nameof(password));
        if (string.IsNullOrWhiteSpace(outputArchivePath))
            throw new ArgumentException("Output archive path is required.", nameof(outputArchivePath));

        string encryptedArchivePath = Path.Combine(backupFolder, EncryptedArchiveFileName);
        if (!File.Exists(encryptedArchivePath))
            throw new FileNotFoundException("Encrypted backup artifact not found.", encryptedArchivePath);

        EncryptedArchiveHeader header = ReadEncryptedArchiveHeader(encryptedArchivePath);
        byte[] saltBytes = Convert.FromBase64String(header.Metadata.SaltBase64);
        byte[] ivBytes = Convert.FromBase64String(header.Metadata.IvBase64);
        int iterations = Math.Max(10_000, header.Metadata.KdfIterations);

        byte[] keyMaterial = new byte[DerivedKeyLengthBytes];
        byte[] encryptionKey = new byte[EncryptionKeyLengthBytes];
        byte[] hmacKey = new byte[HmacKeyLengthBytes];

        string? outputDirectory = Path.GetDirectoryName(outputArchivePath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        string tempOutputPath = outputArchivePath + ".tmp";
        try
        {
            byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
                password,
                saltBytes,
                iterations,
                HashAlgorithmName.SHA256,
                keyMaterial.Length);
            Buffer.BlockCopy(derived, 0, keyMaterial, 0, keyMaterial.Length);
            Buffer.BlockCopy(keyMaterial, 0, encryptionKey, 0, encryptionKey.Length);
            Buffer.BlockCopy(keyMaterial, encryptionKey.Length, hmacKey, 0, hmacKey.Length);

            byte[] computedHmac = ComputeFileHmac(
                encryptedArchivePath,
                hmacKey,
                ct,
                bytesToRead: header.HeaderBytes + header.CipherTextBytes);
            if (!CryptographicOperations.FixedTimeEquals(computedHmac, header.StoredHmac))
            {
                throw new InvalidOperationException(InvalidPasswordOrCorruptedMessage);
            }

            using (var input = new FileStream(encryptedArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(tempOutputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var aes = Aes.Create())
            {
                if (aes is null)
                    throw new InvalidOperationException("AES encryption provider is not available.");

                input.Position = header.HeaderBytes;
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using ICryptoTransform decryptor = aes.CreateDecryptor(encryptionKey, ivBytes);
                using var cryptoStream = new CryptoStream(output, decryptor, CryptoStreamMode.Write, leaveOpen: true);

                long remaining = header.CipherTextBytes;
                byte[] buffer = new byte[1024 * 1024];
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = input.Read(buffer, 0, toRead);
                    if (read <= 0)
                        throw new InvalidDataException("Encrypted backup archive is truncated.");

                    cryptoStream.Write(buffer, 0, read);
                    remaining -= read;
                }

                cryptoStream.FlushFinalBlock();
            }

            if (File.Exists(outputArchivePath))
                File.Delete(outputArchivePath);
            File.Move(tempOutputPath, outputArchivePath);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(InvalidPasswordOrCorruptedMessage, ex);
        }
        finally
        {
            if (File.Exists(tempOutputPath))
            {
                try
                {
                    File.Delete(tempOutputPath);
                }
                catch
                {
                    // best-effort cleanup
                }
            }

            CryptographicOperations.ZeroMemory(keyMaterial);
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(hmacKey);
            CryptographicOperations.ZeroMemory(saltBytes);
            CryptographicOperations.ZeroMemory(ivBytes);
        }
    }

    private static BackupArchiveEnvelopeMetadata BuildMetadata(
        string algorithm,
        string kdfProfile,
        string kdfParamRef,
        int iterations,
        byte[] salt,
        byte[] iv)
    {
        return new BackupArchiveEnvelopeMetadata
        {
            EnvelopeVersion = CurrentEnvelopeVersion,
            FormatVersion = BackupCryptoDescriptor.CurrentFormatVersion,
            Magic = EnvelopeMagic,
            Algorithm = algorithm,
            KdfProfile = kdfProfile,
            KdfParamRef = kdfParamRef,
            KdfIterations = iterations,
            SaltBase64 = Convert.ToBase64String(salt),
            IvBase64 = Convert.ToBase64String(iv),
            HmacAlgorithm = "hmac-sha256"
        };
    }

    private static void WriteEncryptedArchive(
        string plainArchivePath,
        string encryptedArchivePath,
        string password,
        BackupArchiveEnvelopeMetadata metadata,
        byte[] saltBytes,
        byte[] ivBytes,
        CancellationToken ct)
    {
        string metadataJson = JsonSerializer.Serialize(metadata, JsonOptions);
        byte[] metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
        byte[] magicBytes = Encoding.ASCII.GetBytes(EnvelopeMagic);
        byte[] metadataLengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(metadataLengthBytes, metadataBytes.Length);

        int iterations = Math.Max(10_000, metadata.KdfIterations);

        byte[] keyMaterial = new byte[DerivedKeyLengthBytes];
        byte[] encryptionKey = new byte[EncryptionKeyLengthBytes];
        byte[] hmacKey = new byte[HmacKeyLengthBytes];

        try
        {
            byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
                password,
                saltBytes,
                iterations,
                HashAlgorithmName.SHA256,
                keyMaterial.Length);
            Buffer.BlockCopy(derived, 0, keyMaterial, 0, keyMaterial.Length);
            Buffer.BlockCopy(keyMaterial, 0, encryptionKey, 0, encryptionKey.Length);
            Buffer.BlockCopy(keyMaterial, encryptionKey.Length, hmacKey, 0, hmacKey.Length);

            using (var source = new FileStream(plainArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(encryptedArchivePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(magicBytes, 0, magicBytes.Length);
                output.Write(metadataLengthBytes, 0, metadataLengthBytes.Length);
                output.Write(metadataBytes, 0, metadataBytes.Length);

                using var aes = Aes.Create()
                    ?? throw new InvalidOperationException("AES encryption provider is not available.");

                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using ICryptoTransform encryptor = aes.CreateEncryptor(encryptionKey, ivBytes);
                using var cryptoStream = new CryptoStream(output, encryptor, CryptoStreamMode.Write, leaveOpen: true);
                source.CopyTo(cryptoStream);
                cryptoStream.FlushFinalBlock();
            }

            byte[] hmac = ComputeFileHmac(encryptedArchivePath, hmacKey, ct);
            using (var output = new FileStream(encryptedArchivePath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                output.Write(hmac, 0, hmac.Length);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial);
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(hmacKey);
        }
    }

    private static byte[] ComputeFileHmac(string encryptedArchivePath, byte[] key, CancellationToken ct, long bytesToRead = -1)
    {
        using var hmac = new HMACSHA256(key);
        using var stream = new FileStream(encryptedArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[1024 * 1024];
        long remaining = bytesToRead < 0
            ? stream.Length
            : Math.Min(bytesToRead, stream.Length);

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, requested);
            if (read <= 0)
            {
                if (bytesToRead >= 0)
                    throw new InvalidDataException("Encrypted backup archive is truncated.");
                break;
            }

            hmac.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }

        hmac.TransformFinalBlock([], 0, 0);
        byte[] hash = hmac.Hash ?? [];
        if (hash.Length != HmacLengthBytes)
            throw new InvalidOperationException("HMAC generation failed for encrypted backup artifact.");

        return hash;
    }

    private static EncryptedArchiveHeader ReadEncryptedArchiveHeader(string encryptedArchivePath)
    {
        using var stream = new FileStream(encryptedArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < EnvelopeMagic.Length + sizeof(int) + HmacLengthBytes + 1)
            throw new InvalidDataException("Encrypted backup archive is malformed.");

        byte[] magicBytes = ReadExact(stream, EnvelopeMagic.Length);
        string magic = Encoding.ASCII.GetString(magicBytes);
        if (!string.Equals(magic, EnvelopeMagic, StringComparison.Ordinal))
            throw new InvalidDataException("Encrypted backup archive header is invalid.");

        byte[] metadataLengthBytes = ReadExact(stream, sizeof(int));
        int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(metadataLengthBytes);
        if (metadataLength <= 0 || metadataLength > 256 * 1024)
            throw new InvalidDataException("Encrypted backup metadata length is invalid.");

        byte[] metadataBytes = ReadExact(stream, metadataLength);
        string metadataJson = Encoding.UTF8.GetString(metadataBytes);
        BackupArchiveEnvelopeMetadata metadata = JsonSerializer.Deserialize<BackupArchiveEnvelopeMetadata>(metadataJson, JsonOptions)
            ?? throw new InvalidDataException("Encrypted backup metadata is invalid.");

        if (!string.Equals(metadata.Magic, EnvelopeMagic, StringComparison.Ordinal))
            throw new InvalidDataException("Encrypted backup metadata signature mismatch.");

        if (string.IsNullOrWhiteSpace(metadata.SaltBase64) || string.IsNullOrWhiteSpace(metadata.IvBase64))
            throw new InvalidDataException("Encrypted backup metadata is incomplete.");

        int headerBytes = EnvelopeMagic.Length + sizeof(int) + metadataLength;
        long cipherTextBytes = stream.Length - headerBytes - HmacLengthBytes;
        if (cipherTextBytes <= 0)
            throw new InvalidDataException("Encrypted backup payload is empty.");

        stream.Position = stream.Length - HmacLengthBytes;
        byte[] storedHmac = ReadExact(stream, HmacLengthBytes);
        return new EncryptedArchiveHeader(metadata, headerBytes, cipherTextBytes, storedHmac);
    }

    private static byte[] ReadExact(Stream stream, int length)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = stream.Read(buffer, offset, length - offset);
            if (read <= 0)
                throw new InvalidDataException("Unexpected end of encrypted backup data.");
            offset += read;
        }

        return buffer;
    }

    private sealed record EncryptedArchiveHeader(
        BackupArchiveEnvelopeMetadata Metadata,
        long HeaderBytes,
        long CipherTextBytes,
        byte[] StoredHmac);

    private static string ResolveAlgorithm(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string normalized = configured.Trim();
            if (string.Equals(normalized, "aes-256-cbc-hmac-sha256-v1", StringComparison.OrdinalIgnoreCase))
                return "aes-256-cbc-hmac-sha256-v1";
        }

        return "aes-256-cbc-hmac-sha256-v1";
    }

    private static string ResolveKdfProfile(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string normalized = configured.Trim();
            if (normalized.StartsWith("pbkdf2", StringComparison.OrdinalIgnoreCase))
                return normalized;
        }

        return "pbkdf2-sha256-v1";
    }

    private static int ResolveKdfIterations(string? kdfParamRef)
    {
        if (string.IsNullOrWhiteSpace(kdfParamRef))
            return DefaultKdfIterations;

        const string marker = "iter-";
        int idx = kdfParamRef.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return DefaultKdfIterations;

        string valuePart = kdfParamRef[(idx + marker.Length)..].Trim();
        if (int.TryParse(valuePart, out int parsed))
            return Math.Clamp(parsed, 10_000, 1_000_000);

        return DefaultKdfIterations;
    }

    private static string ResolveKdfParamRef(string? configured, int iterations)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return $"pbkdf2-iter-{iterations}";
    }
}

public sealed class BackupArchiveEnvelopeMetadata
{
    public int EnvelopeVersion { get; set; } = BackupArchiveCryptoService.CurrentEnvelopeVersion;
    public int FormatVersion { get; set; } = BackupCryptoDescriptor.CurrentFormatVersion;
    public string Magic { get; set; } = BackupArchiveCryptoService.EnvelopeMagic;
    public string Algorithm { get; set; } = "aes-256-cbc-hmac-sha256-v1";
    public string KdfProfile { get; set; } = "pbkdf2-sha256-v1";
    public string KdfParamRef { get; set; } = "pbkdf2-iter-210000";
    public int KdfIterations { get; set; } = 210_000;
    public string SaltBase64 { get; set; } = string.Empty;
    public string IvBase64 { get; set; } = string.Empty;
    public string HmacAlgorithm { get; set; } = "hmac-sha256";
}
