using System;

namespace VaultSync.Core.Models;

public enum ProtectionActivityPhase
{
    Unknown,
    Queued,
    Preparing,
    Scanning,
    Hashing,
    Writing,
    Compressing,
    Uploading,
    Verifying,
    Waiting,
    Retrying,
    Finalizing,
    Restoring,
    Deleting,
    Cancelling,
    Completed,
    Cancelled,
    Failed
}

public enum ProtectionActivityTone
{
    Neutral,
    Active,
    Attention,
    Success,
    Error
}

public sealed record ProtectionActivityState
{
    public ProtectionActivityState(
        ProtectionActivityPhase phase,
        double? progress = null,
        int? attempt = null,
        int? maxAttempts = null)
    {
        if (progress is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(progress), "Progress must be between 0 and 100.");
        if (attempt is <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempt), "Attempt must be positive when supplied.");
        if (maxAttempts is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Maximum attempts must be positive when supplied.");
        if (attempt.HasValue && maxAttempts.HasValue && attempt.Value > maxAttempts.Value)
            throw new ArgumentException("Attempt cannot exceed maximum attempts.", nameof(attempt));

        Phase = phase;
        Progress = phase == ProtectionActivityPhase.Completed ? 100d : progress;
        Attempt = attempt;
        MaxAttempts = maxAttempts;
    }

    public ProtectionActivityPhase Phase
    {
        get;
    }

    public double? Progress
    {
        get;
    }

    public int? Attempt
    {
        get;
    }

    public int? MaxAttempts
    {
        get;
    }

    public bool IsTerminal => Phase is
        ProtectionActivityPhase.Completed or
        ProtectionActivityPhase.Cancelled or
        ProtectionActivityPhase.Failed;

    public bool IsSuccessful => Phase == ProtectionActivityPhase.Completed;

    public bool IsIndeterminate => !Progress.HasValue || Phase is
        ProtectionActivityPhase.Queued or
        ProtectionActivityPhase.Preparing or
        ProtectionActivityPhase.Waiting or
        ProtectionActivityPhase.Retrying or
        ProtectionActivityPhase.Finalizing or
        ProtectionActivityPhase.Cancelling;

    public bool CanCancel => !IsTerminal && Phase is not
        ProtectionActivityPhase.Finalizing and not
        ProtectionActivityPhase.Cancelling;

    public ProtectionActivityTone Tone => Phase switch
    {
        ProtectionActivityPhase.Completed => ProtectionActivityTone.Success,
        ProtectionActivityPhase.Failed or ProtectionActivityPhase.Cancelled => ProtectionActivityTone.Error,
        ProtectionActivityPhase.Retrying or ProtectionActivityPhase.Waiting or ProtectionActivityPhase.Cancelling =>
            ProtectionActivityTone.Attention,
        ProtectionActivityPhase.Unknown or ProtectionActivityPhase.Queued or ProtectionActivityPhase.Preparing =>
            ProtectionActivityTone.Neutral,
        _ => ProtectionActivityTone.Active
    };

    public bool CanTransitionTo(ProtectionActivityPhase nextPhase)
    {
        if (nextPhase == Phase)
            return true;
        if (IsTerminal)
            return false;
        if (Phase == ProtectionActivityPhase.Cancelling)
            return nextPhase is ProtectionActivityPhase.Cancelled or ProtectionActivityPhase.Failed;

        return nextPhase != ProtectionActivityPhase.Unknown;
    }
}
