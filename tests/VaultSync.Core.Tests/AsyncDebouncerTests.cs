using System;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.CLI.Utils;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class AsyncDebouncerTests
{
    [Fact]
    public async Task Trigger_RunsOnlyTheLatestPendingWork()
    {
        var debouncer = new AsyncDebouncer(40);
        int firstRuns = 0;
        int secondRuns = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        debouncer.Trigger(_ =>
        {
            Interlocked.Increment(ref firstRuns);
            return Task.CompletedTask;
        });
        debouncer.Trigger(_ =>
        {
            Interlocked.Increment(ref secondRuns);
            completed.TrySetResult();
            return Task.CompletedTask;
        });

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(80, CancellationToken.None);

        Assert.Equal(0, firstRuns);
        Assert.Equal(1, secondRuns);
    }

    [Fact]
    public async Task Cancel_PreventsPendingWorkFromRunning()
    {
        var debouncer = new AsyncDebouncer(50);
        int runs = 0;

        debouncer.Trigger(_ =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });
        debouncer.Cancel();

        await Task.Delay(100, CancellationToken.None);

        Assert.Equal(0, runs);
    }
}
