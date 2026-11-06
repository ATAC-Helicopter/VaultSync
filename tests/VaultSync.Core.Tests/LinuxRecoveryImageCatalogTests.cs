#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryImageCatalogTests
{
    private sealed class Source(IEnumerable<string> names) : LinuxRecoveryImageCatalog.ISource
    {
        public IEnumerable<string> Names => names;
        internal readonly List<string> Inspected = [];
        internal bool Changed;
        internal int Checks;
        public LinuxRecoveryImageCatalog.Image? Inspect(string name) { Inspected.Add(name); return new(name, 4096); }
        public void Recheck() { if (++Checks == 2 && Changed) throw new IOException(); }
        public void Dispose() { }
    }
    [Theory]
    [InlineData(".hidden")] [InlineData("../image")] [InlineData("00000000000000000000000000000000")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] [InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    public void Discovery_never_probes_noncanonical_or_hidden_names(string name)
    {
        using var source = new Source([name]);
        var result = LinuxRecoveryImageCatalog.Read(source, CancellationToken.None);
        Assert.Equal(LinuxRecoveryImageCatalog.State.Available, result.Status);
        Assert.Empty(result.Images); Assert.Empty(source.Inspected);
    }
    [Fact]
    public void Seventeenth_entry_refuses_entire_list_including_hidden_entries()
    {
        using var source = new Source(Enumerable.Range(0, 17).Select(_ => Guid.NewGuid().ToString("N")));
        var result = LinuxRecoveryImageCatalog.Read(source, CancellationToken.None);
        Assert.Equal(LinuxRecoveryImageCatalog.State.Failed, result.Status); Assert.Empty(result.Images);
        Assert.Equal(16, source.Inspected.Count);
    }
    [Fact]
    public void Changed_root_discards_candidates()
    {
        using var source = new Source([Guid.NewGuid().ToString("N")]) { Changed = true };
        var result = LinuxRecoveryImageCatalog.Read(source, CancellationToken.None);
        Assert.Equal(LinuxRecoveryImageCatalog.State.Failed, result.Status); Assert.Empty(result.Images);
    }
    [Fact]
    public void Cancelled_refresh_never_returns_candidates()
    {
        using var source = new Source([Guid.NewGuid().ToString("N")]);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => LinuxRecoveryImageCatalog.Read(source, stop.Token));
        Assert.Empty(source.Inspected);
    }
    [Theory]
    [InlineData("directory-listable")] [InlineData("directory-owner")] [InlineData("metadata-symlink")]
    [InlineData("metadata-hardlink")] [InlineData("metadata-size")] [InlineData("payload-writable")]
    [InlineData("payload-device")] [InlineData("payload-empty")] [InlineData("same-inode")]
    public void Candidate_checks_reject_unsafe_files_without_parsing_metadata(string fault)
    {
        var directory = new LinuxRecoveryNative.Identity(0x41c8, 0, 60999, 2, 1, 0, 8, 1, 0, 0, 0, 0);
        var metadata = directory with { Mode = 0x8120, Links = 1, Inode = 2, Size = 128 };
        var payload = metadata with { Inode = 3, Size = 4096 };
        Assert.True(LinuxRecoveryImageCatalog.Admits(directory, metadata, payload));
        switch (fault)
        {
            case "directory-listable": directory = directory with { Mode = 0x41e8 }; break;
            case "directory-owner": directory = directory with { Owner = 60999 }; break;
            case "metadata-symlink": metadata = metadata with { Mode = 0xa120 }; break;
            case "metadata-hardlink": metadata = metadata with { Links = 2 }; break;
            case "metadata-size": metadata = metadata with { Size = ulong.MaxValue }; break;
            case "payload-writable": payload = payload with { Mode = 0x81a0 }; break;
            case "payload-device": payload = payload with { DeviceMinor = 2 }; break;
            case "payload-empty": payload = payload with { Size = 0 }; break;
            case "same-inode": payload = payload with { Inode = 2 }; break;
        }
        Assert.False(LinuxRecoveryImageCatalog.Admits(directory, metadata, payload));
    }
}
