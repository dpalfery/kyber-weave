namespace KyberWeave.Mcp;

/// <summary>
/// How the docs server was asked to start: bound to the one root it resolves at startup, or in any-repo mode, where it
/// holds no root and every call names the repository it is about with <c>projectPath</c>.
/// </summary>
/// <param name="AnyRepo">True when <c>--any-repo</c> was passed.</param>
/// <param name="Conflict">Why the arguments cannot be served, or null when they can.</param>
/// <remarks>
/// <c>--any-repo</c> with <c>--repo-root</c> or <c>--expect-root</c> is refused rather than one flag winning: each of
/// those names a root, and an any-repo server has none, so the operator meant one of two different servers and the
/// process cannot tell which.
/// </remarks>
public sealed record McpServeMode(bool AnyRepo, string? Conflict)
{
    /// <summary>The flag that starts the server in any-repo mode.</summary>
    public const string AnyRepoFlag = "--any-repo";

    /// <summary>The rule id the startup refusal carries on stderr when the flags conflict.</summary>
    public const string ConflictRuleId = "KW-MCP-ROOT-003";

    private const string RepoRootFlag = "--repo-root";
    private const string ExpectRootFlag = "--expect-root";

    /// <summary>The stderr line for a conflict, <c>KW-MCP-ROOT-003: &lt;message&gt;</c>, or null when there is none.</summary>
    public string? ConflictDiagnostic => Conflict is null ? null : $"{ConflictRuleId}: {Conflict}";

    /// <summary>Reads the mode from the process arguments.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>Bound mode without <c>--any-repo</c>; any-repo mode with it; a conflict when a root flag is also present.</returns>
    public static McpServeMode Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool anyRepo = args.Contains(AnyRepoFlag, StringComparer.Ordinal);
        if (!anyRepo) return new McpServeMode(false, null);

        string? rootFlag = args.Select(RootFlagOf).FirstOrDefault(flag => flag is not null);
        if (rootFlag is null) return new McpServeMode(true, null);

        return new McpServeMode(
            true,
            $"{AnyRepoFlag} cannot be combined with {rootFlag}: an any-repo server holds no root, and every call names "
            + $"its repository with projectPath. Remove {rootFlag} to serve any repository, or remove {AnyRepoFlag} "
            + "to serve the one root.");
    }

    private static string? RootFlagOf(string argument)
    {
        foreach (string flag in (string[])[RepoRootFlag, ExpectRootFlag])
        {
            if (argument == flag || argument.StartsWith(flag + "=", StringComparison.Ordinal)) return flag;
        }

        return null;
    }
}
