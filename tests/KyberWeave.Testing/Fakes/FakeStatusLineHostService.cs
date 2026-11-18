using System.Security.Cryptography;
using KyberWeave.Cli.Commands.Utilities;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.StatusLine;

namespace KyberWeave.Tests.Fakes;

/// <summary>The status-line host service in a unit test: the PATH, file state and artifact tree a test states.</summary>
/// <remarks>
/// Every answer has a healthy default — a file matches its digest and carries its executable bit — so a case states
/// only the fact it turns unhealthy. Files are keyed by the full physical path, or by a portable relative path that the
/// physical path ends with, so two receipt entries with the same file name in different directories stay distinct. A
/// file whose bytes the test states is compared by digest, exactly as the real host does; the other files answer with
/// the scripted state, and the digest each evaluation was asked about is recorded so a command that passes the wrong
/// one is visible.
/// </remarks>
public sealed class FakeStatusLineHostService : IStatusLineHostService
{
    private readonly List<string> _pathProbes = [];
    private readonly List<(string PhysicalPath, string ExpectedSha256)> _evaluations = [];
    private readonly List<(string StagingRoot, IReadOnlyList<StatusLineDeploymentFile> Deployed)> _executableBitApplications = [];

    /// <summary>The executable names a <c>PATH</c> lookup finds.</summary>
    public HashSet<string> Executables { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The state each file answers with, keyed by full path or by a portable relative path suffix. A file absent here,
    /// and absent from <see cref="Contents"/>, is <see cref="UtilitiesStatusLineFileState.Ok"/>.
    /// </summary>
    public Dictionary<string, UtilitiesStatusLineFileState> FileStates { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The bytes a file holds, keyed like <see cref="FileStates"/>. When a file's bytes are stated, its state is the
    /// digest comparison against them, which is what the real host does.
    /// </summary>
    public Dictionary<string, byte[]> Contents { get; } = new(StringComparer.Ordinal);

    /// <summary>The file names, or portable relative path suffixes, whose executable bit is missing.</summary>
    public HashSet<string> FilesWithoutExecutableBit { get; } = new(StringComparer.Ordinal);

    /// <summary>The artifact tree a deploy opens; empty unless a test states one.</summary>
    public FakeStatusLineArtifactSource Artifacts { get; set; } = new();

    /// <summary>The failure <see cref="ResolveArtifactSource"/> throws, or null when it answers with <see cref="Artifacts"/>.</summary>
    public Exception? ArtifactSourceFailure { get; set; }

    /// <summary>The executable names <see cref="IsOnPath"/> was asked about, in call order.</summary>
    public IReadOnlyList<string> PathProbes => _pathProbes;

    /// <summary>The physical paths <see cref="EvaluateOwnedFile"/> was asked about, in call order.</summary>
    public IReadOnlyList<string> EvaluatedFiles => [.. _evaluations.Select(evaluation => evaluation.PhysicalPath)];

    /// <summary>Each digest comparison, as the physical path and the digest the host was asked to compare against.</summary>
    public IReadOnlyList<(string PhysicalPath, string ExpectedSha256)> Evaluations => _evaluations;

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
        _evaluations.Add((physicalPath, expectedSha256));

        if (TryFind(Contents, physicalPath, out byte[]? content))
        {
            string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
            return string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
                ? UtilitiesStatusLineFileState.Ok
                : UtilitiesStatusLineFileState.Drift;
        }

        return TryFind(FileStates, physicalPath, out UtilitiesStatusLineFileState state)
            ? state
            : UtilitiesStatusLineFileState.Ok;
    }

    /// <inheritdoc />
    public bool HasExecutableBit(string physicalPath) =>
        !Matches(FilesWithoutExecutableBit, physicalPath);

    /// <inheritdoc />
    public void ApplyExecutableBit(string stagingRoot, IReadOnlyList<StatusLineDeploymentFile> deployed) =>
        _executableBitApplications.Add((stagingRoot, deployed));

    private static bool TryFind<T>(Dictionary<string, T> values, string physicalPath, out T value)
    {
        if (values.TryGetValue(physicalPath, out value!)) return true;

        foreach (KeyValuePair<string, T> pair in values)
            if (MatchesPortableSuffix(physicalPath, pair.Key))
            {
                value = pair.Value;
                return true;
            }

        value = default!;
        return false;
    }

    private static bool Matches(IEnumerable<string> keys, string physicalPath) =>
        keys.Any(key => MatchesPortableSuffix(physicalPath, key));

    private static bool MatchesPortableSuffix(string physicalPath, string key)
    {
        string portablePath = physicalPath.Replace('\\', '/');
        string portableKey = key.Replace('\\', '/').Trim('/');
        return portablePath.EndsWith("/" + portableKey, StringComparison.Ordinal);
    }
}
