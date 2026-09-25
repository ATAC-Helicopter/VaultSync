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
    [Fact]
    public async Task DisposalDrainsSupersededWorkAndRejectsLaterTriggers()
    {
        var debouncer = new AsyncDebouncer(0);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool finished = false;
        debouncer.Trigger(async token =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                cancelled.SetResult();
                await release.Task;
                finished = true;
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        debouncer.Trigger(_ => Task.CompletedTask);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task shutdown = debouncer.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        release.SetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(finished);
        int laterRuns = 0;
        debouncer.Trigger(_ => { laterRuns++; return Task.CompletedTask; });
        await debouncer.DisposeAsync();
        Assert.Equal(0, laterRuns);
    }

    [Fact]
    public async Task ConcurrentCompletionCancellationAndTriggersDoNotDisposeLiveTokenSources()
    {
        await using var debouncer = new AsyncDebouncer(0);
        await Task.WhenAll(Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
                debouncer.Trigger(_ => Task.CompletedTask);
        }), Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
                debouncer.Cancel();
        }));
    }
}
