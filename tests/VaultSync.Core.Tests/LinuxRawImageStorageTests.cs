#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace VaultSync.Core.Tests;

public sealed class LinuxRawImageStorageTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("/tmp/../capture")]
    [InlineData("/tmp/./capture")]
    [InlineData("/tmp/\0capture")]
    public void InvalidRootsCannotCreateOutput(string path)
    {
        Assert.Throws<IOException>(() => LinuxRawImageStorage.Create(path, Guid.NewGuid(), 65536));
    }

    [Fact]
    public async Task NativeCreateOnlyPublicationAndCollisionPreserveExistingBytes()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            output.WriteLine("Native ext4 transaction not executed on this platform; VM evidence is separate.");
            Assert.Throws<IOException>(() => LinuxRawImageStorage.Create("/", Guid.NewGuid(), 65536));
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), "vaultsync-raw-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Guid id = Guid.NewGuid();
            LinuxRawImageStorage first;
            try { first = LinuxRawImageStorage.Create(root, id, 65536); }
            catch (IOException ex) when (ex.Message == "raw-storage.private-ext4-space-required")
            {
                output.WriteLine("This Linux filesystem refused the ext4/private/space policy; native success not established here.");
                Assert.Empty(Directory.GetFileSystemEntries(root));
                return;
            }
            await using (first)
            {
                Assert.Equal(0, first.Payload.Length); // KEEP_SIZE preallocation.
                Assert.False(Directory.Exists(Path.Combine(root, id.ToString("N"))));
                await first.Payload.WriteAsync(new byte[] { 1, 2, 3 });
                await first.Metadata.WriteAsync(new byte[] { 4, 5, 6 });
                first.FlushPayload(); first.FlushMetadata(); first.FlushDirectories(); first.Publish();
                Assert.True(first.Published);
                Assert.Throws<IOException>(first.Publish);
            }
            string final = Path.Combine(root, id.ToString("N"));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(final, "payload.raw")));
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(final, "image.metadata")));
            await using (var collision = LinuxRawImageStorage.Create(root, id, 65536))
            {
                Assert.Throws<IOException>(collision.Publish);
                Assert.False(collision.Published);
            }
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(final, "payload.raw")));
            Assert.Single(Directory.GetDirectories(root, ".vaultsync-incomplete-*"));
            Guid emptyId = Guid.NewGuid();
            string emptyTarget = Path.Combine(root, emptyId.ToString("N"));
            Directory.CreateDirectory(emptyTarget, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using (var emptyCollision = LinuxRawImageStorage.Create(root, emptyId, 65536))
            {
                Assert.Throws<IOException>(emptyCollision.Publish);
                Assert.False(emptyCollision.Published);
                Assert.Empty(Directory.GetFileSystemEntries(emptyTarget));
            }
            Assert.Throws<IOException>(() => LinuxRawImageStorage.Create(root, Guid.NewGuid(), DiskImageRawProfileVerificationService.MaximumCapacity));
            string link = Path.Combine(root, "linked-parent"); Directory.CreateSymbolicLink(link, root);
            Assert.Throws<IOException>(() => LinuxRawImageStorage.Create(link, Guid.NewGuid(), 65536));
            string publicDir = Path.Combine(root, "public-parent"); Directory.CreateDirectory(publicDir);
            File.SetUnixFileMode(publicDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            Assert.Throws<IOException>(() => LinuxRawImageStorage.Create(publicDir, Guid.NewGuid(), 65536));
            Assert.Empty(Directory.GetFileSystemEntries(publicDir));
            Guid stageId = Guid.NewGuid();
            await using (var staging = LinuxRawImageStorage.Create(root, stageId, 65536))
            {
                await staging.Payload.WriteAsync(new byte[] { 7, 8, 9 });
                await staging.Metadata.WriteAsync(new byte[] { 10, 11, 12 });
                staging.Seal();
                Assert.False(staging.Payload.CanWrite); Assert.False(staging.Metadata.CanWrite);
                Assert.Throws<NotSupportedException>(() => staging.Payload.WriteByte(1));
                Assert.Throws<IOException>(staging.Seal);
                Assert.Equal(7, staging.Payload.ReadByte());
                staging.FlushDirectories(); staging.Publish();
                Assert.Throws<IOException>(staging.Seal);
            }
            string staged = Path.Combine(root, stageId.ToString("N"));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(Path.Combine(staged, "payload.raw")));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(Path.Combine(staged, "image.metadata")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
