using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.StatusLine;

namespace KyberWeave.Tests.Fakes;

/// <summary>The status-line artifact source in a unit test: the artifact sets a test states, and nothing on disk.</summary>
/// <remarks>
/// A target with no stated set answers with an empty list, which is what a deploy reports as an artifact set
/// without a primary file rather than as a read failure.
/// </remarks>
public sealed class FakeStatusLineArtifactSource : IUtilitiesStatusLineArtifactSource
{
    private readonly Dictionary<StatusLineTarget, IReadOnlyList<StatusLineDeploymentFile>> _files = [];

    /// <summary>States the artifacts a deploy reads for <paramref name="target"/>.</summary>
    /// <param name="target">The harness the artifacts belong to.</param>
    /// <param name="files">The artifacts to answer with.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeStatusLineArtifactSource With(StatusLineTarget target, params StatusLineDeploymentFile[] files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files[target] = files;
        return this;
    }

    /// <inheritdoc />
    public IReadOnlyList<StatusLineDeploymentFile> Load(StatusLineTarget target) =>
        _files.TryGetValue(target, out IReadOnlyList<StatusLineDeploymentFile>? files) ? files : [];
}
