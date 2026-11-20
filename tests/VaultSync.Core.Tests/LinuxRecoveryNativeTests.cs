#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryNativeTests
{
    [Fact]
    public void HeldRegularFilesPreserveIdentityAndRejectLinksAliasesAndChangedContent()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var directory = Directory.CreateTempSubdirectory("vsn-");
        string path = Path.Combine(directory.FullName, "owned"), link = Path.Combine(directory.FullName, "alias");
        try
        {
            File.WriteAllText(path, "fixture");
            using var held = LinuxRecoveryNative.OpenFile(path, false);
            var before = LinuxRecoveryNative.Stat(held.SafeFileHandle);
            Assert.Equal(before, LinuxRecoveryNative.StatPath(path));
            Assert.Equal(7UL, before.Size);
            Assert.True(LinuxRecoveryNative.NoCapabilities(held.SafeFileHandle));
            Assert.False(LinuxRecoveryNative.ReadOnly(held.SafeFileHandle));
            Assert.False(LinuxRecoveryNative.Cgroup(held.SafeFileHandle));
            File.WriteAllText(path, "fixture-expanded");
            Assert.NotEqual(before, LinuxRecoveryNative.Stat(held.SafeFileHandle));
            File.CreateSymbolicLink(link, path);
            Assert.Throws<InvalidDataException>(() => { using var file = LinuxRecoveryNative.OpenFile(link, false); });
            Assert.Throws<InvalidDataException>(() => { using var file = LinuxRecoveryNative.Open(path + "/../owned", 0); });
            Assert.Throws<InvalidDataException>(() => { using var file = LinuxRecoveryNative.Open("relative", 0); });
            Assert.Throws<InvalidDataException>(() => { using var file = LinuxRecoveryNative.OpenFile(directory.FullName, false); });
            Assert.Throws<InvalidDataException>(() => { using var file = LinuxRecoveryNative.OpenFile(path, true); });
            Assert.Throws<InvalidDataException>(() => { using var file = LinuxRecoveryNative.OpenDirectory(path, false); });
        }
        finally { File.Delete(link); File.Delete(path); directory.Delete(); }
    }

    [Fact]
    public void HeldDirectoryMutationsAndExclusiveLocksStayInsideOwnedFixture()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var directory = Directory.CreateTempSubdirectory("vsn-");
        string path = Path.Combine(directory.FullName, "lock");
        try
        {
            using var parent = LinuxRecoveryNative.Open(directory.FullName, 0x10000 | 0x20000 | 0x80000);
            LinuxRecoveryNative.MakeDirectory(parent, "child");
            Assert.True(Directory.Exists(Path.Combine(directory.FullName, "child")));
            Assert.Throws<IOException>(() => LinuxRecoveryNative.MakeDirectory(parent, "child"));
            LinuxRecoveryNative.Sync(parent);
            LinuxRecoveryNative.Remove(parent, "child", true);
            Assert.False(Directory.Exists(Path.Combine(directory.FullName, "child")));
            Assert.Throws<IOException>(() => LinuxRecoveryNative.Remove(parent, "missing", false));
            using (var first = LinuxRecoveryNative.Open(path, 2 | 0x40 | 0x80 | 0x20000 | 0x80000, 0x180))
            using (var second = LinuxRecoveryNative.Open(path, 2 | 0x20000 | 0x80000))
            {
                LinuxRecoveryNative.Lock(first);
                Assert.Throws<IOException>(() => LinuxRecoveryNative.Lock(second));
                LinuxRecoveryNative.Sync(first);
            }
            using (var next = LinuxRecoveryNative.Open(path, 2 | 0x20000 | 0x80000)) LinuxRecoveryNative.Lock(next);
            LinuxRecoveryNative.Remove(parent, "lock", false);
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); directory.Delete(); }
    }

    [Fact]
    public void BoundedKernelTextReaderAndProcessIdentityFailClosed()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var directory = Directory.CreateTempSubdirectory("vsn-"); string path = Path.Combine(directory.FullName, "text");
        try
        {
            File.WriteAllText(path, "1234", new UTF8Encoding(false));
            Assert.Equal("1234", LinuxRecoveryNative.ReadBounded(path, 4));
            Assert.Throws<InvalidDataException>(() => LinuxRecoveryNative.ReadBounded(path, 3));
            File.WriteAllBytes(path, new byte[] { 0xff });
            Assert.Throws<DecoderFallbackException>(() => LinuxRecoveryNative.ReadBounded(path, 4));
            using var executable = LinuxRecoveryNative.OpenProcExecutable(Environment.ProcessId);
            Assert.Equal(LinuxRecoveryNative.Stat(executable.SafeFileHandle), LinuxRecoveryNative.ExecutableIdentity(Environment.ProcessId));
            Assert.True(LinuxRecoveryNative.ParentProcess() > 0); Assert.True(LinuxRecoveryNative.Session(0) > 0);
            Assert.Throws<InvalidDataException>(() => { using var file = LinuxRecoveryNative.OpenProcExecutable(0); });
            Assert.Throws<InvalidDataException>(() => LinuxRecoveryNative.StatPath(path + ".missing"));
            using var invalid = new SafeFileHandle((IntPtr)(-1), false);
            Assert.Throws<InvalidDataException>(() => LinuxRecoveryNative.Stat(invalid));
            Assert.Throws<IOException>(() => LinuxRecoveryNative.Sync(invalid));
        }
        finally { File.Delete(path); directory.Delete(); }
    }
}
