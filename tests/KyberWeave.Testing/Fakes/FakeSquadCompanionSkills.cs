using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Testing.Fakes;

/// <summary>
/// The companion skills as a script, so a pack runs without a checkout: it records the roots it was asked about
/// and reports the files the test names.
/// </summary>
public sealed class FakeSquadCompanionSkills : ISquadCompanionSkills
{
    private readonly List<(string RelativePath, string SourcePath)> _files;
    private readonly List<(string SquadSourceRoot, string? CompanionSkillsRoot)> _calls = [];

    /// <summary>Creates the fake with no companion skill files.</summary>
    public FakeSquadCompanionSkills()
        : this([])
    {
    }

    /// <summary>Creates the fake with the given companion skill files.</summary>
    /// <param name="files">Each file's Squad-tree path and its source path.</param>
    public FakeSquadCompanionSkills(params (string RelativePath, string SourcePath)[] files) => _files = [.. files];

    /// <summary>The roots every call asked about, as <c>(squadSourceRoot, companionSkillsRoot)</c>.</summary>
    public IReadOnlyList<(string SquadSourceRoot, string? CompanionSkillsRoot)> Calls => _calls;

    /// <inheritdoc />
    public string? ResolveRoot(string squadSourceRoot) => null;

    /// <inheritdoc />
    public IReadOnlyList<(string RelativePath, string SourcePath)> Collect(
        string squadSourceRoot,
        string? companionSkillsRoot)
    {
        _calls.Add((squadSourceRoot, companionSkillsRoot));
        return _files;
    }
}
