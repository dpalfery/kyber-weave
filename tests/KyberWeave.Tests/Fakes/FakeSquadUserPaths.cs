using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Testable fake for <see cref="ISquadUserPaths"/> providing an isolated application data directory.
/// </summary>
public sealed class FakeSquadUserPaths : ISquadUserPaths
{
    public FakeSquadUserPaths(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ApplicationDataDirectory = applicationDataDirectory;
    }

    public string ApplicationDataDirectory { get; }
}
