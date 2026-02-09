using VaultSync.Core.Config;
using VaultSync.Core.Models;
using System.Collections.Generic;

namespace VaultSync.Core.Services;

public static class BackupEncryptionPolicyResolver
{
    public sealed record ResolvedPolicy(
        bool EncryptionRequested,
        string EffectivePolicy,
        string? EffectiveKeyRef,
        string KeySource);

    public static ResolvedPolicy Resolve(Project project, BackupEncryptionConfig config)
    {
        var effectivePolicy = ProjectEncryptionPolicy.Normalize(project.EncryptionPolicy);
        var encryptionRequested = ProjectEncryptionPolicy.IsEncrypted(effectivePolicy, config.Enabled);
        if (!encryptionRequested)
        {
            return new ResolvedPolicy(false, effectivePolicy, null, "none");
        }

        var projectKeyRef = string.IsNullOrWhiteSpace(project.EncryptionKeyRef)
            ? null
            : project.EncryptionKeyRef.Trim();
        if (!string.IsNullOrWhiteSpace(projectKeyRef))
        {
            return new ResolvedPolicy(true, effectivePolicy, projectKeyRef, "project");
        }

        var globalKeyRef = string.IsNullOrWhiteSpace(config.KeyRef)
            ? null
            : config.KeyRef.Trim();
        return new ResolvedPolicy(true, effectivePolicy, globalKeyRef, "global");
    }

    public static IReadOnlyList<string> ResolveRestoreKeyRefs(Project project, BackupEncryptionConfig config)
    {
        var ordered = new List<string>(2);

        var projectKeyRef = string.IsNullOrWhiteSpace(project.EncryptionKeyRef)
            ? null
            : project.EncryptionKeyRef.Trim();
        if (!string.IsNullOrWhiteSpace(projectKeyRef))
            ordered.Add(projectKeyRef);

        var globalKeyRef = string.IsNullOrWhiteSpace(config.KeyRef)
            ? null
            : config.KeyRef.Trim();
        if (!string.IsNullOrWhiteSpace(globalKeyRef) &&
            !string.Equals(projectKeyRef, globalKeyRef, System.StringComparison.OrdinalIgnoreCase))
        {
            ordered.Add(globalKeyRef);
        }

        return ordered;
    }
}
