#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupArchiveCryptoServiceTests
{
    [Fact]
    public void EncryptArchiveInPlace_WritesEncryptedArtifact_AndRemovesPlainArchive()
    {
        using var root = new TempDirectory();
        string backupFolder = BackupArchiveTestFactory.CreatePlainBackupFolder(root.Path);
        var config = new BackupEncryptionConfig { Enabled = true };

        BackupArchiveCryptoService.EncryptionResult result = BackupArchiveCryptoService.EncryptArchiveInPlace(backupFolder, "test-password", config);

        string plainArchive = Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
        string encryptedArchive = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
        string metadataPath = Path.Combine(backupFolder, BackupArchiveCryptoService.MetadataFileName);

        Assert.True(result.IsEncrypted);
        Assert.False(File.Exists(plainArchive));
        Assert.True(File.Exists(encryptedArchive));
        Assert.True(File.Exists(metadataPath));
        Assert.NotEqual("none", result.Descriptor.Algorithm);
    }

    [Fact]
    public void EncryptedArchive_IsNotReadableAsPlainZip()
    {
        using var root = new TempDirectory();
        string backupFolder = BackupArchiveTestFactory.CreatePlainBackupFolder(root.Path);
        var config = new BackupEncryptionConfig { Enabled = true };
        BackupArchiveCryptoService.EncryptionResult result = BackupArchiveCryptoService.EncryptArchiveInPlace(backupFolder, "test-password", config);

        Assert.ThrowsAny<Exception>(() =>
        {
            using ZipArchive _ = ZipFile.OpenRead(result.EncryptedArchivePath);
        });
    }

    [Fact]
    public void TryReadDescriptor_WhenEncryptedMetadataExists_ReturnsEncryptedDescriptor()
    {
        using var root = new TempDirectory();
        string backupFolder = BackupArchiveTestFactory.CreateEncryptedBackupFolder(root.Path, "test-password");

        bool found = BackupArchiveCryptoService.TryReadDescriptor(backupFolder, out Models.BackupCryptoDescriptor? descriptor, out bool isEncrypted);

        Assert.True(found);
        Assert.True(isEncrypted);
        Assert.NotEqual("none", descriptor.Algorithm);
    }

    [Fact]
    public void GetStoredArchiveSize_PrefersEncryptedArtifact()
    {
        using var root = new TempDirectory();
        string backupFolder = BackupArchiveTestFactory.CreatePlainBackupFolder(root.Path);
        var config = new BackupEncryptionConfig { Enabled = true };
        BackupArchiveCryptoService.EncryptionResult result = BackupArchiveCryptoService.EncryptArchiveInPlace(backupFolder, "test-password", config);

        long size = BackupArchiveCryptoService.GetStoredArchiveSize(backupFolder);

        Assert.Equal(result.EncryptedBytes, size);
        Assert.True(size > 0);
    }

    [Fact]
    public void DecryptArchiveToPlainZip_WithValidPassword_RecreatesReadableArchive()
    {
        using var root = new TempDirectory();
        string backupFolder = BackupArchiveTestFactory.CreateEncryptedBackupFolder(root.Path, "test-password");

        string restoredArchive = Path.Combine(backupFolder, "restored.zip");
        BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "test-password", restoredArchive);

        Assert.True(File.Exists(restoredArchive));
        using ZipArchive archive = ZipFile.OpenRead(restoredArchive);
        ZipArchiveEntry? entry = archive.GetEntry(BackupArchiveTestFactory.DefaultEntryName);
        Assert.NotNull(entry);
        using Stream stream = entry!.Open();
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        Assert.Equal(BackupArchiveTestFactory.DefaultContent, text);
    }

    [Fact]
    public void DecryptArchiveToPlainZip_WithWrongPassword_FailsWithExplicitError_AndNoPartialOutput()
    {
        using var root = new TempDirectory();
        string backupFolder = BackupArchiveTestFactory.CreateEncryptedBackupFolder(root.Path, "test-password");

        string restoredArchive = Path.Combine(backupFolder, "restored.zip");
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "wrong-password", restoredArchive));

        Assert.Equal(BackupArchiveCryptoService.InvalidPasswordOrCorruptedMessage, ex.Message);
        Assert.False(File.Exists(restoredArchive));
        Assert.False(File.Exists(restoredArchive + ".tmp"));
    }

    [Fact]
    public void DecryptArchiveToPlainZip_WhenCiphertextIsTampered_FailsWithoutPlaintextOutput()
    {
        using var root = new TempDirectory();
        string backupFolder = BackupArchiveTestFactory.CreateEncryptedBackupFolder(root.Path, "test-password");
        string encryptedArchive = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
        byte[] bytes = File.ReadAllBytes(encryptedArchive);
        bytes[^40] ^= 0x5A;
        File.WriteAllBytes(encryptedArchive, bytes);

        string restoredArchive = Path.Combine(backupFolder, "tampered.zip");
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "test-password", restoredArchive));

        Assert.Equal(BackupArchiveCryptoService.InvalidPasswordOrCorruptedMessage, ex.Message);
        Assert.False(File.Exists(restoredArchive));
        Assert.False(File.Exists(restoredArchive + ".tmp"));
    }

    [Theory]
    [InlineData("kdfIterations", 1000001)]
    [InlineData("formatVersion", 2)]
    [InlineData("envelopeVersion", 2)]
    public void DecryptArchiveToPlainZip_RejectsUnsupportedEmbeddedParameters(string propertyName, int value)
    {
        using var root = new TempDirectory();
        string backupFolder = BackupArchiveTestFactory.CreateEncryptedBackupFolder(root.Path, "test-password");
        RewriteEnvelopeMetadata(
            Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName),
            propertyName,
            JsonValueKind.Number,
            value.ToString());

        string restoredArchive = Path.Combine(backupFolder, "unsupported.zip");
        Assert.Throws<InvalidDataException>(() =>
            BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "test-password", restoredArchive));
        Assert.False(File.Exists(restoredArchive));
    }

    [Theory]
    [InlineData("algorithm", "aes-unknown")]
    [InlineData("kdfProfile", "argon2id-v99")]
    [InlineData("hmacAlgorithm", "none")]
    public void DecryptArchiveToPlainZip_RejectsUnsupportedFormatIdentifiers(string propertyName, string value)
    {
        using var root = new TempDirectory();
        string backupFolder = BackupArchiveTestFactory.CreateEncryptedBackupFolder(root.Path, "test-password");
        RewriteEnvelopeMetadata(
            Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName),
            propertyName,
            JsonValueKind.String,
            value);

        string restoredArchive = Path.Combine(backupFolder, "unsupported.zip");
        Assert.Throws<InvalidDataException>(() =>
            BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "test-password", restoredArchive));
        Assert.False(File.Exists(restoredArchive));
    }

    [Fact]
    public void EncryptArchiveInPlace_WhenCancelled_LeavesPlainArchiveAndRemovesPartialEncryptedArtifacts()
    {
        using var root = new TempDirectory();
        string backupFolder = BackupArchiveTestFactory.CreatePlainBackupFolder(root.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            BackupArchiveCryptoService.EncryptArchiveInPlace(
                backupFolder,
                "test-password",
                new BackupEncryptionConfig { Enabled = true },
                cts.Token));

        Assert.True(File.Exists(Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName)));
        Assert.False(File.Exists(Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName)));
        Assert.False(File.Exists(Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName + ".tmp")));
        Assert.False(File.Exists(Path.Combine(backupFolder, BackupArchiveCryptoService.MetadataFileName + ".tmp")));
    }

    [Fact]
    public void CopyStreamWithCancellation_WhenCancelledDuringRead_PublishesNoFurtherBytes()
    {
        using var cts = new CancellationTokenSource();
        using var source = new CancelOnFirstReadStream(
            Enumerable.Range(0, 256).Select(value => (byte)value).ToArray(),
            cts);
        using var destination = new MemoryStream();

        Assert.Throws<OperationCanceledException>(() =>
            BackupArchiveCryptoService.CopyStreamWithCancellation(
                source,
                destination,
                bufferSize: 64,
                cts.Token));

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(0, destination.Length);
    }

    private static void RewriteEnvelopeMetadata(
        string encryptedArchivePath,
        string propertyName,
        JsonValueKind valueKind,
        string value)
    {
        byte[] original = File.ReadAllBytes(encryptedArchivePath);
        int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(
            original.AsSpan(BackupArchiveCryptoService.EnvelopeMagic.Length, sizeof(int)));
        int metadataOffset = BackupArchiveCryptoService.EnvelopeMagic.Length + sizeof(int);
        using JsonDocument document = JsonDocument.Parse(
            original.AsMemory(metadataOffset, metadataLength));
        var values = document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach ((string name, JsonElement element) in values)
            {
                writer.WritePropertyName(name);
                if (string.Equals(name, propertyName, StringComparison.Ordinal))
                {
                    if (valueKind == JsonValueKind.Number)
                        writer.WriteNumberValue(int.Parse(value));
                    else
                        writer.WriteStringValue(value);
                }
                else
                {
                    element.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        byte[] metadata = stream.ToArray();
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes(BackupArchiveCryptoService.EnvelopeMagic));
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, metadata.Length);
        output.Write(length);
        output.Write(metadata);
        output.Write(original, metadataOffset + metadataLength, original.Length - metadataOffset - metadataLength);
        File.WriteAllBytes(encryptedArchivePath, output.ToArray());
    }

    private sealed class CancelOnFirstReadStream(byte[] buffer, CancellationTokenSource cts) : MemoryStream(buffer)
    {
        public int ReadCount { get; private set; }

        public override int Read(byte[] destination, int offset, int count)
        {
            int read = base.Read(destination, offset, count);
            ReadCount++;
            cts.Cancel();
            return read;
        }
    }
}
