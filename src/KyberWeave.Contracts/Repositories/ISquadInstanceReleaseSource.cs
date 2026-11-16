using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Contracts.Repositories;

/// <summary>The release source the harness-instance service installs from, which prefers a local checkout over a download.</summary>
public interface ISquadInstanceReleaseSource : ISquadReleaseSource;
