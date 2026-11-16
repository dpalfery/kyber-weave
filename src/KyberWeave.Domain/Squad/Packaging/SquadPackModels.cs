namespace KyberWeave.Core.Squad.Packaging;

/// <summary>What a <c>squad pack</c> run was asked to do.</summary>
/// <param name="Format">The archive format: <c>apm</c>, <c>plugins</c> or <c>all</c>.</param>
/// <param name="Version">The version to pack, or blank for <paramref name="DefaultVersion"/>.</param>
/// <param name="OutDirectory">Where the archives go, or blank for <c>artifacts</c> under the working directory.</param>
/// <param name="DefaultVersion">The version of the running binary, used when no version was given.</param>
public sealed record SquadPackRequest(string Format, string? Version, string? OutDirectory, string DefaultVersion);

/// <summary>How a <c>squad pack</c> run ended.</summary>
public enum SquadPackStatus
{
    /// <summary>The archives were written.</summary>
    Packed,

    /// <summary>The format is not <c>apm</c>, <c>plugins</c> or <c>all</c>.</summary>
    InvalidFormat,

    /// <summary>The working directory is not the repository root that holds the canonical Squad source.</summary>
    NotRepositoryRoot,

    /// <summary>The source or the composed tree was refused, or an archive could not be written.</summary>
    Failed
}

/// <summary>The result of a <c>squad pack</c> run.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="Error">The failure message, for <see cref="SquadPackStatus.Failed"/>.</param>
/// <param name="ApmArchive">The APM archive, when one was written.</param>
/// <param name="PluginsArchive">The Agent Plugins archive, when one was written.</param>
/// <param name="ChecksumFile">The checksum file, when the format was <c>all</c>.</param>
/// <param name="Warnings">Warnings raised while packing, in the order they occurred.</param>
public sealed record SquadPackOutcome(
    SquadPackStatus Status,
    string? Error,
    string? ApmArchive,
    string? PluginsArchive,
    string? ChecksumFile,
    IReadOnlyList<string> Warnings);
