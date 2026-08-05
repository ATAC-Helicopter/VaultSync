using System;
using VaultSync.Core.Models;

namespace VaultSync.UI.ViewModels;

internal static class ProtectionActivityClassifier
{
    public static ProtectionActivityPhase Classify(double progress, string? detail, string? status)
    {
        if (Contains(detail, "fail") || Contains(status, "fail") ||
            Contains(detail, "error") || Contains(status, "error"))
        {
            return ProtectionActivityPhase.Failed;
        }

        if (Contains(detail, "cancelled") || Contains(status, "cancelled"))
            return ProtectionActivityPhase.Cancelled;
        if (Contains(detail, "cancelling") || Contains(status, "cancelling"))
            return ProtectionActivityPhase.Cancelling;
        if (Contains(detail, "retry") || Contains(status, "retry"))
            return ProtectionActivityPhase.Retrying;
        if (Contains(detail, "queued") || Contains(status, "queued"))
            return ProtectionActivityPhase.Queued;
        if (Contains(detail, "waiting") || Contains(status, "waiting") || Contains(status, "stalled"))
            return ProtectionActivityPhase.Waiting;
        if (Contains(detail, "verif") || Contains(status, "verif"))
            return ProtectionActivityPhase.Verifying;
        if (Contains(detail, "scanning") || Contains(status, "scanning"))
            return ProtectionActivityPhase.Scanning;
        if (Contains(detail, "hashing") || Contains(status, "hashing") ||
            Contains(detail, "creating snapshot") || Contains(detail, "reusing existing snapshot"))
        {
            return ProtectionActivityPhase.Hashing;
        }

        if (Contains(detail, "finalizing") || Contains(status, "finalizing"))
            return ProtectionActivityPhase.Finalizing;
        if (Contains(detail, "compressing") || Contains(status, "compressing") ||
            Contains(detail, "encrypting") || Contains(status, "encrypting"))
        {
            return ProtectionActivityPhase.Compressing;
        }

        if (Contains(detail, "uploading") || Contains(status, "uploading"))
            return ProtectionActivityPhase.Uploading;
        if (Contains(detail, "restoring") || Contains(status, "restoring") ||
            Contains(detail, "decrypting") || Contains(status, "decrypting"))
        {
            return ProtectionActivityPhase.Restoring;
        }

        if (Contains(detail, "deleting") || Contains(status, "deleting"))
            return ProtectionActivityPhase.Deleting;
        if (progress >= 99.9d || Contains(detail, "completed") || Contains(status, "completed") ||
            Contains(detail, "no changes") || Contains(status, "no changes"))
        {
            return ProtectionActivityPhase.Completed;
        }

        if (Contains(detail, "copying") || Contains(status, "copying") || progress > 0.1d)
            return ProtectionActivityPhase.Writing;

        if (Contains(detail, "preparing") || Contains(status, "preparing") ||
            Contains(detail, "estimating") || Contains(status, "estimating"))
        {
            return ProtectionActivityPhase.Preparing;
        }

        return ProtectionActivityPhase.Unknown;
    }

    private static bool Contains(string? value, string token) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(token, StringComparison.OrdinalIgnoreCase);
}
