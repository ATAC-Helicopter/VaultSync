#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PageLocationWriterTests
{
    [Fact]
    public async Task SlowWriteCannotOverwriteTheNewestRequestedLocation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        var writer = new PageLocationWriter(async page =>
        {
            if (page == "Projects")
            {
                started.SetResult();
                await release.Task;
            }
            writes.Add(page);
        }, _ => throw new InvalidOperationException("Unexpected write failure"));
        Task drained = writer.Enqueue("Projects");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = writer.Enqueue("Backups");
        _ = writer.Enqueue("History");
        Task newest = writer.Enqueue("Settings");
        release.SetResult();
        await Task.WhenAll(drained, newest).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "Projects", "Settings" }, writes);
    }

    [Fact]
    public async Task FailedWriteAndDiagnosticDoNotStrandNewerLocations()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        int failures = 0;
        var writer = new PageLocationWriter(async page =>
        {
            if (page == "Projects")
            {
                started.SetResult();
                await release.Task;
                throw new InvalidOperationException("Store failure");
            }
            writes.Add(page);
        }, _ => { failures++; throw new InvalidOperationException("Diagnostic failure"); });
        Task drained = writer.Enqueue("Projects");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task newest = writer.Enqueue("Recovery");
        release.SetResult();
        await Task.WhenAll(drained, newest).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, failures);
        Assert.Equal(new[] { "Recovery" }, writes);
    }

    [Fact]
    public async Task NewRequestsAfterDrainingStartANewWriter()
    {
        var writes = new List<string>();
        var writer = new PageLocationWriter(page => { writes.Add(page); return Task.CompletedTask; }, _ => { });
        await writer.Enqueue("Projects").WaitAsync(TimeSpan.FromSeconds(5));
        await writer.Enqueue("Settings").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "Projects", "Settings" }, writes);
    }

    [Fact]
    public void UnknownPagesAreRejectedBeforeAnyWrite()
    {
        var writer = new PageLocationWriter(_ => throw new InvalidOperationException("Unexpected write"), _ => { });
        Assert.Throws<ArgumentException>(() => { _ = writer.Enqueue("private/path"); });
    }
}
