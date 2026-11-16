using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>The outcome of loading a repository's host configuration: the configuration, or why it did not load.</summary>
/// <param name="Success">Whether the configuration loaded. A repository with no configuration file loads as the product defaults.</param>
/// <param name="Config">The configuration; null when <paramref name="Success"/> is false.</param>
/// <param name="ConfigPath">The file the configuration came from, or null when there is none.</param>
/// <param name="Error">Why the file did not load; null on success.</param>
public sealed record HostConfigLoadResult(bool Success, KyberWeaveConfig? Config, string? ConfigPath, string? Error);
