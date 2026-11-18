using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Arbiter.Mcp;

/// <summary>Resolves the repository the <c>serve</c> process answers for.</summary>
/// <remarks>
/// Order: <c>--repo-root</c>, then <c>KYBER_WEAVE_REPO_ROOT</c>, then the working
/// directory. Unlike the docs server, the root need not hold host configuration:
/// <c>arbiter_check_configuration_and_key_readiness</c> reports a missing configuration rather than refusing to start,
/// so a client can see why it cannot evaluate. The root must exist, because a missing
/// directory would make every ledger and log path resolve somewhere unintended.
/// </remarks>
/// <param name="files">Probes the resolved root for a directory.</param>
/// <param name="paths">Turns the flag, the variable or the working directory into a full path.</param>
/// <param name="environment">Names the directory a relative value is resolved against.</param>
public sealed class ArbiterRootResolver(
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment)
{
    /// <summary>The environment variable naming the root when the flag is absent.</summary>
    public const string EnvironmentVariable = "KYBER_WEAVE_REPO_ROOT";

    /// <summary>Resolves the absolute root for one serve process.</summary>
    /// <exception cref="ArgumentException">A <c>--repo-root</c> flag carries no path.</exception>
    /// <exception cref="InvalidOperationException">The resolved root is not a directory.</exception>
    public string Resolve(IReadOnlyList<string> args, string workingDirectory, string? environmentRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string baseDirectory = paths.GetFullPath(workingDirectory, environment.CurrentDirectory);
        string? explicitRoot = ExplicitRoot(args);
        string candidate = explicitRoot is not null
            ? paths.GetFullPath(explicitRoot, baseDirectory)
            : !string.IsNullOrWhiteSpace(environmentRoot)
                ? paths.GetFullPath(environmentRoot, baseDirectory)
                : baseDirectory;

        if (!files.DirectoryExists(candidate))
        {
            throw new InvalidOperationException($"The Arbiter root '{candidate}' is not an existing directory.");
        }

        return candidate;
    }

    private static string? ExplicitRoot(IReadOnlyList<string> args)
    {
        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            if (argument == "--repo-root")
            {
                if (index == args.Count - 1 || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    throw new ArgumentException("--repo-root requires a repository path.", nameof(args));
                }

                return args[index + 1];
            }

            const string prefix = "--repo-root=";
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                string value = argument[prefix.Length..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("--repo-root requires a repository path.", nameof(args));
                }

                return value;
            }
        }

        return null;
    }
}
