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
}
