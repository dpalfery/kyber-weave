using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;

namespace KyberWeave.Application.RunStart;

/// <summary>Opens run-start runners over the registered git, file system and process environment.</summary>
public sealed class RunStartRunnerFactory(IGitCommandRunner git, IWritableFileSystem files, IProcessEnvironment environment, IPathResolver paths) : IRunStartRunnerFactory
{
    /// <inheritdoc />
    public IRunStartRunner Open(string repositoryRoot) => new RunStartRunner(git, files, environment, paths, repositoryRoot);
}
