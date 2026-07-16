using System;
using System.Threading.Tasks;
using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task ExecuteAsync_PreventsOverlappingExecution_AndRestoresCanExecute()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int executionCount = 0;
        var command = new AsyncRelayCommand(async _ =>
        {
            executionCount++;
            started.SetResult();
            await release.Task;
        });

        Task first = command.ExecuteAsync();
        await started.Task;

        Assert.True(command.IsExecuting);
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(1, executionCount);

        release.SetResult();
        await first;

        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task ExecuteAsync_ObservesFailure_AndAllowsRetry()
    {
        int executionCount = 0;
        var expected = new InvalidOperationException("test failure");
        var command = new AsyncRelayCommand(_ =>
        {
            executionCount++;
            return executionCount == 1 ? Task.FromException(expected) : Task.CompletedTask;
        }, operationName: "test-command");

        await command.ExecuteAsync();

        Assert.Same(expected, command.LastException);
        Assert.True(command.CanExecute(null));

        await command.ExecuteAsync();
        Assert.Null(command.LastException);
        Assert.Equal(2, executionCount);
    }

    [Fact]
    public async Task ExecuteAsync_TreatsCancellationAsExpectedOutcome()
    {
        var command = new AsyncRelayCommand(_ => Task.FromCanceled(new System.Threading.CancellationToken(canceled: true)));

        await command.ExecuteAsync();

        Assert.Null(command.LastException);
        Assert.True(command.CanExecute(null));
    }
}
