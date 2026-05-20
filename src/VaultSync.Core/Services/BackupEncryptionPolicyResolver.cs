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
        string effectivePolicy = ProjectEncryptionPolicy.Normalize(project.EncryptionPolicy);
        bool encryptionRequested = ProjectEncryptionPolicy.IsEncrypted(effectivePolicy, config.Enabled);
        if (!encryptionRequested)
        {
            return new ResolvedPolicy(false, effectivePolicy, null, "none");
        }

        string? projectKeyRef = string.IsNullOrWhiteSpace(project.EncryptionKeyRef)
            ? null
            : project.EncryptionKeyRef.Trim();
        if (!string.IsNullOrWhiteSpace(projectKeyRef))
        {
            return new ResolvedPolicy(true, effectivePolicy, projectKeyRef, "project");
        }

        string? globalKeyRef = string.IsNullOrWhiteSpace(config.KeyRef)
            ? null
            : config.KeyRef.Trim();
        return new ResolvedPolicy(true, effectivePolicy, globalKeyRef, "global");
    }

    public static IReadOnlyList<string> ResolveRestoreKeyRefs(Project project, BackupEncryptionConfig config)
    {
        var ordered = new List<string>(2);

        string? projectKeyRef = string.IsNullOrWhiteSpace(project.EncryptionKeyRef)
            ? null
            : project.EncryptionKeyRef.Trim();
        if (!string.IsNullOrWhiteSpace(projectKeyRef))
            ordered.Add(projectKeyRef);

        string? globalKeyRef = string.IsNullOrWhiteSpace(config.KeyRef)
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
