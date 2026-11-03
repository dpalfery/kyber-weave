namespace KyberWeave.Arbiter.Mcp;

/// <summary>Resolves the repository the <c>serve</c> process answers for.</summary>
/// <remarks>
/// Order: <c>--repo-root</c>, then <c>KYBER_WEAVE_REPO_ROOT</c>, then the working
/// directory. Unlike the docs server, the root need not hold host configuration:
/// <c>arbiter_status</c> reports a missing configuration rather than refusing to start,
/// so a client can see why it cannot evaluate. The root must exist, because a missing
/// directory would make every ledger and log path resolve somewhere unintended.
/// </remarks>
public static class ArbiterRootResolver
{
    /// <summary>The environment variable naming the root when the flag is absent.</summary>
    public const string EnvironmentVariable = "KYBER_WEAVE_REPO_ROOT";

    /// <summary>Resolves the absolute root for one serve process.</summary>
    /// <exception cref="ArgumentException">A <c>--repo-root</c> flag carries no path.</exception>
    /// <exception cref="InvalidOperationException">The resolved root is not a directory.</exception>
    public static string Resolve(IReadOnlyList<string> args, string workingDirectory, string? environmentRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string baseDirectory = Path.GetFullPath(workingDirectory);
        string? explicitRoot = ExplicitRoot(args);
        string candidate = explicitRoot is not null
            ? Path.GetFullPath(explicitRoot, baseDirectory)
            : !string.IsNullOrWhiteSpace(environmentRoot)
                ? Path.GetFullPath(environmentRoot, baseDirectory)
                : baseDirectory;

        if (!Directory.Exists(candidate))
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
