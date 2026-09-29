using KyberWeave.Core.Configuration;

namespace KyberWeave.Mcp;

/// <summary>Resolves the repository that an MCP process is allowed to serve.</summary>
/// <remarks>
/// The working directory is a valid project binding only after <c>docs init</c> has
/// created the host configuration there. Walking to a parent Git repository is
/// deliberately not supported: a desktop harness can start two project-local servers
/// with a shared parent as its working directory, and that guess can silently serve the
/// wrong corpus.
///
/// Portable harness configuration uses <c>--repo-root .</c>. Global or headless
/// configurations can use an explicit path or the generic
/// <c>KYBER_WEAVE_REPO_ROOT</c> override.
/// </remarks>
public static class RepositoryRootResolver
{
    public const string EnvironmentVariable = "KYBER_WEAVE_REPO_ROOT";

    /// <summary>
    /// The environment variable a client can set to assert which absolute root it believes
    /// the server is bound to. Consulted only when <c>--expect-root</c> is absent; the flag
    /// wins over the environment.
    /// </summary>
    public const string ExpectRootEnvironmentVariable = "KYBER_WEAVE_EXPECT_ROOT";

    public static string Resolve(
        IReadOnlyList<string> args,
        string workingDirectory,
        string? environmentRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string baseDirectory = Path.GetFullPath(workingDirectory);
        string resolved = ResolveBoundRoot(args, baseDirectory, environmentRoot);

        // A client-asserted expectation is a safety rail, not part of binding: the root is
        // resolved first (so the error can name both paths) and only then compared. It
        // catches the reported failure mode where a same-named server is pinned to another
        // checkout and answers silently.
        string? expected = ExpectRootArgument(args);
        if (expected is not null)
        {
            string expectedRoot = ResolvePath(expected, baseDirectory);
            if (!PathsEqual(expectedRoot, resolved))
            {
                throw new InvalidOperationException(
                    $"The --expect-root value '{expectedRoot}' does not match the resolved MCP " +
                    $"root '{resolved}'. Refusing to serve because the client expects a different " +
                    "repository and answers would come from the wrong corpus. Pass the matching " +
                    "root, or drop --expect-root to accept whatever root resolves.");
            }
        }

        return resolved;
    }

    /// <summary>Whether the argument list supplies a <c>--expect-root</c> flag.</summary>
    public static bool HasExpectRootArgument(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return ExpectRootArgument(args) is not null;
    }

    /// <summary>
    /// Ordinal path equality after <see cref="Path.GetFullPath(string)"/> normalisation.
    /// Symlinks are not resolved, so a temp path reported differently by the OS is still
    /// compared exactly as the caller supplied it.
    /// </summary>
    public static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.Ordinal);

    private static string ResolveBoundRoot(
        IReadOnlyList<string> args,
        string baseDirectory,
        string? environmentRoot)
    {
        string? explicitRoot = ExplicitRoot(args);
        if (explicitRoot is not null)
        {
            return RequireInitializedRoot(
                ResolvePath(explicitRoot, baseDirectory),
                "--repo-root");
        }

        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            return RequireInitializedRoot(
                ResolvePath(environmentRoot, baseDirectory),
                EnvironmentVariable);
        }

        return RequireInitializedRoot(baseDirectory, "the working directory");
    }

    private static string? ExpectRootArgument(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string argument = args[i];
            if (argument == "--expect-root")
            {
                if (i == args.Count - 1 || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    throw new ArgumentException(
                        "--expect-root requires a repository path.",
                        nameof(args));
                }

                return args[i + 1];
            }

            const string prefix = "--expect-root=";
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                string value = argument[prefix.Length..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException(
                        "--expect-root requires a repository path.",
                        nameof(args));
                }

                return value;
            }
        }

        return null;
    }

    private static string? ExplicitRoot(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string argument = args[i];
            if (argument == "--repo-root")
            {
                if (i == args.Count - 1 || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    throw new ArgumentException(
                        "--repo-root requires a repository path.",
                        nameof(args));
                }

                return args[i + 1];
            }

            const string prefix = "--repo-root=";
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                string value = argument[prefix.Length..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException(
                        "--repo-root requires a repository path.",
                        nameof(args));
                }

                return value;
            }
        }

        return null;
    }

    private static string ResolvePath(string path, string baseDirectory) =>
        Path.GetFullPath(path, baseDirectory);

    private static string RequireInitializedRoot(string root, string source)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root);
        if (loaded.ConfigPath is not null)
        {
            return root;
        }

        throw new InvalidOperationException(
            $"No Kyber-Weave host configuration was found for MCP root '{root}' " +
            $"selected by {source}. Run 'kyber-weave docs init \"{root}\"' once, " +
            "then launch the server from that project or pass --repo-root with its " +
            "path. The MCP server will not guess a parent repository.");
    }
}
