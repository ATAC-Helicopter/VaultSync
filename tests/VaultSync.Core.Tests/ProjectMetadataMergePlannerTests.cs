using VaultSync.Core.Config;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProjectMetadataMergePlannerTests
{
    [Fact]
    public void Create_MergesIndependentFieldEdits()
    {
        ProjectMetadataConflictValues mergeBase = Values(tags: "base", restoreMode: "direct");
        ProjectMetadataConflictValues local = Values(tags: "local", restoreMode: "direct");
        ProjectMetadataConflictValues imported = Values(tags: "base", restoreMode: "staged");

        ProjectMetadataMergePlan plan = ProjectMetadataMergePlanner.Create(mergeBase, local, imported);

        Assert.False(plan.HasConflicts);
        Assert.Equal("local", plan.Merged.Tags);
        Assert.Equal("staged", plan.Merged.RestoreMode);
    }

    [Fact]
    public void Create_FlagsOnlyOverlappingEdits()
    {
        ProjectMetadataConflictValues mergeBase = Values(tags: "base", restoreMode: "direct");
        ProjectMetadataConflictValues local = Values(tags: "local", restoreMode: "direct");
        ProjectMetadataConflictValues imported = Values(tags: "remote", restoreMode: "staged");

        ProjectMetadataMergePlan plan = ProjectMetadataMergePlanner.Create(mergeBase, local, imported);

        Assert.Equal(["tags"], plan.ConflictingFields);
        Assert.Equal("local", plan.KeepLocalResult.Tags);
        Assert.Equal("remote", plan.AcceptImportedResult.Tags);
        Assert.Equal("staged", plan.KeepLocalResult.RestoreMode);
        Assert.Equal("staged", plan.AcceptImportedResult.RestoreMode);
    }

    [Fact]
    public void Create_DoesNotConflictWhenBothMachinesMadeTheSameEdit()
    {
        ProjectMetadataConflictValues mergeBase = Values(tags: "base");
        ProjectMetadataConflictValues local = Values(tags: "shared");
        ProjectMetadataConflictValues imported = Values(tags: "shared");

        ProjectMetadataMergePlan plan = ProjectMetadataMergePlanner.Create(mergeBase, local, imported);

        Assert.False(plan.HasConflicts);
        Assert.Equal("shared", plan.Merged.Tags);
    }

    [Fact]
    public void Create_WithoutTrustedBase_RequiresReviewForDifferences()
    {
        ProjectMetadataConflictValues local = Values(tags: "local", restoreMode: "direct");
        ProjectMetadataConflictValues imported = Values(tags: "remote", restoreMode: "staged");

        ProjectMetadataMergePlan plan = ProjectMetadataMergePlanner.Create(null, local, imported);

        Assert.Contains("tags", plan.ConflictingFields);
        Assert.Contains("restoreMode", plan.ConflictingFields);
        Assert.Equal("local", plan.KeepLocalResult.Tags);
        Assert.Equal("remote", plan.AcceptImportedResult.Tags);
    }

    private static ProjectMetadataConflictValues Values(string tags, string restoreMode = "direct") => new()
    {
        AvatarColor = "#123456",
        EncryptionPolicy = "inherit",
        PreferredDestinationId = string.Empty,
        RestoreMode = restoreMode,
        VerificationPolicy = "always",
        AutoBackupEnabled = true,
        Tags = tags
    };
}
