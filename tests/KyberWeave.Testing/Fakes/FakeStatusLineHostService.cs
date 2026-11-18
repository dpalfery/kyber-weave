using KyberWeave.Cli.Commands.Utilities;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.StatusLine;

namespace KyberWeave.Tests.Fakes;

/// <summary>The status-line host service in a unit test: the PATH, file state and artifact tree a test states.</summary>
/// <remarks>
/// Every answer has a healthy default — a file matches its digest and carries its executable bit — so a case states
/// only the fact it turns unhealthy. Files are keyed by name, because a command resolves a receipt's portable relative
/// path against the staging root before it asks about the file.
/// </remarks>
public sealed class FakeStatusLineHostService : IStatusLineHostService
{
    private readonly List<string> _pathProbes = [];
    private readonly List<string> _evaluatedFiles = [];
    private readonly List<(string StagingRoot, IReadOnlyList<StatusLineDeploymentFile> Deployed)> _executableBitApplications = [];

    /// <summary>The executable names a <c>PATH</c> lookup finds.</summary>
    public HashSet<string> Executables { get; } = new(StringComparer.Ordinal);

    /// <summary>The state each file name answers with; a name absent here is <see cref="UtilitiesStatusLineFileState.Ok"/>.</summary>
    public Dictionary<string, UtilitiesStatusLineFileState> FileStates { get; } = new(StringComparer.Ordinal);

    /// <summary>The file names whose executable bit is missing; a name absent here is executable.</summary>
    public HashSet<string> FilesWithoutExecutableBit { get; } = new(StringComparer.Ordinal);

    /// <summary>The artifact tree a deploy opens; empty unless a test states one.</summary>
    public FakeStatusLineArtifactSource Artifacts { get; set; } = new();

    /// <summary>The failure <see cref="ResolveArtifactSource"/> throws, or null when it answers with <see cref="Artifacts"/>.</summary>
    public Exception? ArtifactSourceFailure { get; set; }

    /// <summary>The executable names <see cref="IsOnPath"/> was asked about, in call order.</summary>
    public IReadOnlyList<string> PathProbes => _pathProbes;

    /// <summary>The physical paths <see cref="EvaluateOwnedFile"/> was asked about, in call order.</summary>
    public IReadOnlyList<string> EvaluatedFiles => _evaluatedFiles;

    /// <summary>Each <see cref="ApplyExecutableBit"/> call, as the staging root and the files it was given.</summary>
    public IReadOnlyList<(string StagingRoot, IReadOnlyList<StatusLineDeploymentFile> Deployed)> ExecutableBitApplications =>
        _executableBitApplications;

    /// <inheritdoc />
    public IUtilitiesStatusLineArtifactSource ResolveArtifactSource()
    {
        if (ArtifactSourceFailure is not null) throw ArtifactSourceFailure;
        return Artifacts;
    }

    /// <inheritdoc />
    public bool IsOnPath(string executable)
    {
        _pathProbes.Add(executable);
        return Executables.Contains(executable);
    }

    /// <inheritdoc />
    public UtilitiesStatusLineFileState EvaluateOwnedFile(string physicalPath, string expectedSha256)
    {
        _evaluatedFiles.Add(physicalPath);
        return FileStates.TryGetValue(Path.GetFileName(physicalPath), out UtilitiesStatusLineFileState state)
            ? state
            : UtilitiesStatusLineFileState.Ok;
    }

    /// <inheritdoc />
    public bool HasExecutableBit(string physicalPath) =>
        !FilesWithoutExecutableBit.Contains(Path.GetFileName(physicalPath));

    /// <inheritdoc />
    public void ApplyExecutableBit(string stagingRoot, IReadOnlyList<StatusLineDeploymentFile> deployed) =>
        _executableBitApplications.Add((stagingRoot, deployed));
}
