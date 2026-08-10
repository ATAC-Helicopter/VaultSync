using System;
using VaultSync.Core.Models;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProtectionActivityStateTests
{
    [Theory]
    [InlineData("Scanning project...", "", ProtectionActivityPhase.Scanning)]
    [InlineData("", "Hashing 20/100 files", ProtectionActivityPhase.Hashing)]
    [InlineData("Copying files", "10 MB/s", ProtectionActivityPhase.Writing)]
    [InlineData("", "Verifying backup", ProtectionActivityPhase.Verifying)]
    [InlineData("Queued for destination", "", ProtectionActivityPhase.Queued)]
    [InlineData("", "Waiting for network...", ProtectionActivityPhase.Waiting)]
    [InlineData("Retrying destination", "attempt 2/3", ProtectionActivityPhase.Retrying)]
    [InlineData("Backup failed", "", ProtectionActivityPhase.Failed)]
    [InlineData("", "Error writing destination", ProtectionActivityPhase.Failed)]
    [InlineData("Cancelled", "", ProtectionActivityPhase.Cancelled)]
    [InlineData("", "Cancelling operation", ProtectionActivityPhase.Cancelling)]
    [InlineData("Finalizing", "", ProtectionActivityPhase.Finalizing)]
    [InlineData("Encrypting archive", "", ProtectionActivityPhase.Compressing)]
    [InlineData("", "Uploading archive", ProtectionActivityPhase.Uploading)]
    [InlineData("Decrypting files", "", ProtectionActivityPhase.Restoring)]
    [InlineData("", "Deleting backup", ProtectionActivityPhase.Deleting)]
    [InlineData("No changes detected", "", ProtectionActivityPhase.Completed)]
    [InlineData("Preparing backup", "", ProtectionActivityPhase.Preparing)]
    [InlineData("", "", ProtectionActivityPhase.Unknown)]
    public void Classifier_MapsRequiredProtectionPhases(
        string detail,
        string status,
        ProtectionActivityPhase expected)
    {
        Assert.Equal(expected, ProtectionActivityClassifier.Classify(0, detail, status));
    }

    [Fact]
    public void Classifier_TreatsFinishedProgressAsCompletedBeforeGenericWriting()
    {
        Assert.Equal(
            ProtectionActivityPhase.Completed,
            ProtectionActivityClassifier.Classify(100, string.Empty, string.Empty));
    }

    [Fact]
    public void ProgressItem_UsesSemanticPhaseInsteadOfParsingDisplayText()
    {
        var item = new BackupProgressItem
        {
            CurrentFile = "opaque-localized-status",
            ActivityState = new ProtectionActivityState(ProtectionActivityPhase.Queued)
        };

        Assert.Equal(ProtectionActivityPhase.Queued, item.ActivityPhase);
        Assert.Equal("Queued", item.StageLabel);
        Assert.True(item.IsIndeterminate);
    }

    [Fact]
    public void CompletedState_IsTerminalSuccessfulAndFullyProgressed()
    {
        var state = new ProtectionActivityState(ProtectionActivityPhase.Completed, 42);

        Assert.True(state.IsTerminal);
        Assert.True(state.IsSuccessful);
        Assert.False(state.CanCancel);
        Assert.Equal(100, state.Progress);
        Assert.Equal(ProtectionActivityTone.Success, state.Tone);
    }

    [Fact]
    public void CancellingState_OnlyTransitionsToCancellationOrFailure()
    {
        var state = new ProtectionActivityState(ProtectionActivityPhase.Cancelling);

        Assert.True(state.CanTransitionTo(ProtectionActivityPhase.Cancelled));
        Assert.True(state.CanTransitionTo(ProtectionActivityPhase.Failed));
        Assert.False(state.CanTransitionTo(ProtectionActivityPhase.Writing));
    }

    [Fact]
    public void RetryAttempt_CannotExceedMaximum()
    {
        Assert.Throws<ArgumentException>(() => new ProtectionActivityState(
            ProtectionActivityPhase.Retrying,
            attempt: 4,
            maxAttempts: 3));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    public void Progress_MustStayWithinPercentageBounds(double progress)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProtectionActivityState(
            ProtectionActivityPhase.Writing,
            progress));
    }

    [Fact]
    public void RetryMetadata_MustUsePositiveValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProtectionActivityState(
            ProtectionActivityPhase.Retrying,
            attempt: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProtectionActivityState(
            ProtectionActivityPhase.Retrying,
            maxAttempts: 0));
    }

    [Theory]
    [InlineData(ProtectionActivityPhase.Queued)]
    [InlineData(ProtectionActivityPhase.Preparing)]
    [InlineData(ProtectionActivityPhase.Waiting)]
    [InlineData(ProtectionActivityPhase.Retrying)]
    [InlineData(ProtectionActivityPhase.Finalizing)]
    [InlineData(ProtectionActivityPhase.Cancelling)]
    public void NonProgressPhases_AreIndeterminate(ProtectionActivityPhase phase)
    {
        var state = new ProtectionActivityState(phase, progress: 50);

        Assert.True(state.IsIndeterminate);
    }

    [Theory]
    [InlineData(ProtectionActivityPhase.Completed, ProtectionActivityTone.Success)]
    [InlineData(ProtectionActivityPhase.Cancelled, ProtectionActivityTone.Error)]
    [InlineData(ProtectionActivityPhase.Failed, ProtectionActivityTone.Error)]
    [InlineData(ProtectionActivityPhase.Retrying, ProtectionActivityTone.Attention)]
    [InlineData(ProtectionActivityPhase.Waiting, ProtectionActivityTone.Attention)]
    [InlineData(ProtectionActivityPhase.Cancelling, ProtectionActivityTone.Attention)]
    [InlineData(ProtectionActivityPhase.Unknown, ProtectionActivityTone.Neutral)]
    [InlineData(ProtectionActivityPhase.Queued, ProtectionActivityTone.Neutral)]
    [InlineData(ProtectionActivityPhase.Preparing, ProtectionActivityTone.Neutral)]
    [InlineData(ProtectionActivityPhase.Writing, ProtectionActivityTone.Active)]
    public void Tone_ReflectsSemanticPhase(
        ProtectionActivityPhase phase,
        ProtectionActivityTone expected)
    {
        Assert.Equal(expected, new ProtectionActivityState(phase).Tone);
    }

    [Fact]
    public void TerminalAndFinalizingStates_CannotBeCancelledOrRestarted()
    {
        var failed = new ProtectionActivityState(ProtectionActivityPhase.Failed);
        var cancelled = new ProtectionActivityState(ProtectionActivityPhase.Cancelled);
        var finalizing = new ProtectionActivityState(ProtectionActivityPhase.Finalizing);

        Assert.True(failed.IsTerminal);
        Assert.True(cancelled.IsTerminal);
        Assert.False(failed.IsSuccessful);
        Assert.False(cancelled.IsSuccessful);
        Assert.False(failed.CanCancel);
        Assert.False(finalizing.CanCancel);
        Assert.False(failed.CanTransitionTo(ProtectionActivityPhase.Preparing));
    }

    [Fact]
    public void ActiveState_AllowsValidTransitionsAndRejectsUnknown()
    {
        var state = new ProtectionActivityState(ProtectionActivityPhase.Writing, progress: 20);

        Assert.False(state.IsIndeterminate);
        Assert.True(state.CanCancel);
        Assert.True(state.CanTransitionTo(ProtectionActivityPhase.Writing));
        Assert.True(state.CanTransitionTo(ProtectionActivityPhase.Verifying));
        Assert.False(state.CanTransitionTo(ProtectionActivityPhase.Unknown));
    }
}
