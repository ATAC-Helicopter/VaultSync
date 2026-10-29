#nullable enable
using System;
using System.IO;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryControllerLaunchTests
{
    [Fact]
    public void HelloHasIndependentFixedMagicAndNetworkUuidLayout()
    {
        Guid operation = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        byte[] wire = Convert.FromHexString("56535248454c303100112233445566778899aabbccddeeff");
        Assert.Equal(wire, LinuxRecoveryControllerLaunch.Hello(operation));
        Assert.Equal(operation, LinuxRecoveryControllerLaunch.DecodeHello(wire));
    }

    [Fact]
    public void EmptyWrongMagicTruncatedAndTrailingHellosRefuse()
    {
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.Hello(Guid.Empty));
        byte[] valid = LinuxRecoveryControllerLaunch.Hello(Guid.NewGuid());
        for (int size = 0; size < valid.Length; size++)
        {
            byte[] shortFrame = valid[..size];
            Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.DecodeHello(shortFrame));
        }
        byte[] extra = new byte[25]; valid.CopyTo(extra, 0);
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.DecodeHello(extra));
        valid[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.DecodeHello(valid));
        byte[] empty = LinuxRecoveryControllerLaunch.Hello(Guid.NewGuid()); Array.Clear(empty, 8, 16);
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.DecodeHello(empty));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)]
    public void ReservedOrMissingProcessCannotEnterChildValidation(int process) =>
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.ValidateNonrootProcess(process));
}
