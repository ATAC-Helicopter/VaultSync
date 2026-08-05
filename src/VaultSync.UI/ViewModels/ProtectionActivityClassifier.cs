using System;
using System.Linq;
using VaultSync.Core.Models;

namespace VaultSync.UI.ViewModels;

internal static class ProtectionActivityClassifier
{
    private static readonly ActivityRule[] SemanticRules =
    [
        new(ProtectionActivityPhase.Failed, "fail", "error"),
        new(ProtectionActivityPhase.Cancelled, "cancelled"),
        new(ProtectionActivityPhase.Cancelling, "cancelling"),
        new(ProtectionActivityPhase.Retrying, "retry"),
        new(ProtectionActivityPhase.Queued, "queued"),
        new(ProtectionActivityPhase.Waiting, "waiting", "stalled"),
        new(ProtectionActivityPhase.Verifying, "verif"),
        new(ProtectionActivityPhase.Scanning, "scanning"),
        new(ProtectionActivityPhase.Hashing, "hashing", "creating snapshot", "reusing existing snapshot"),
        new(ProtectionActivityPhase.Finalizing, "finalizing"),
        new(ProtectionActivityPhase.Compressing, "compressing", "encrypting"),
        new(ProtectionActivityPhase.Uploading, "uploading"),
        new(ProtectionActivityPhase.Restoring, "restoring", "decrypting"),
        new(ProtectionActivityPhase.Deleting, "deleting")
    ];

    public static ProtectionActivityPhase Classify(double progress, string? detail, string? status)
    {
        ProtectionActivityPhase? semanticPhase = MatchSemanticRule(detail, status);
        if (semanticPhase.HasValue)
            return semanticPhase.Value;

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

    private static ProtectionActivityPhase? MatchSemanticRule(string? detail, string? status)
    {
        foreach (ActivityRule rule in SemanticRules)
        {
            if (rule.Tokens.Any(token => Contains(detail, token) || Contains(status, token)))
                return rule.Phase;
        }

        return null;
    }

    private static bool Contains(string? value, string token) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private sealed record ActivityRule(ProtectionActivityPhase Phase, params string[] Tokens);
}
