using VaultSync.Core.Models;

namespace VaultSync.Core.Tests.TestSupport;

public sealed class ProjectBuilder
{
    private int _id = 1;
    private string _name = "VaultSync";
    private string _rootPath = @"C:\Repo";
    private string _preset = "default";
    private string _preferredDestinationId = null;
    private string _verificationPolicy = ProjectVerificationPolicy.Always;

    public ProjectBuilder WithId(int id)
    {
        _id = id;
        return this;
    }

    public ProjectBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public ProjectBuilder WithRootPath(string rootPath)
    {
        _rootPath = rootPath;
        return this;
    }

    public ProjectBuilder WithPreset(string preset)
    {
        _preset = preset;
        return this;
    }

    public ProjectBuilder WithPreferredDestinationId(string preferredDestinationId)
    {
        _preferredDestinationId = preferredDestinationId;
        return this;
    }

    public ProjectBuilder WithVerificationPolicy(string policy)
    {
        _verificationPolicy = policy;
        return this;
    }

    public Project Build() =>
        new()
        {
            Id = _id,
            Name = _name,
            RootPath = _rootPath,
            Preset = _preset,
            PreferredDestinationId = _preferredDestinationId,
            VerificationPolicy = _verificationPolicy
        };
}
