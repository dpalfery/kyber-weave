using KyberWeave.Core.Processes;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The removal phase deletes fourteen agents, and no shipped source file may still name one except the two maps
/// that resolve a host's leftover reference and the retired-skill map <c>squad doctor</c> reads to warn about
/// leftover installed copies. A name left in shipped code is a route to an agent that is not installed: a help
/// string that offers it, a dispatch example that uses it, or a validator that branches on it.
/// </summary>
/// <remarks>
/// The scan walks <c>git ls-files</c>, so a tracked file is what a reader, an installer or a rendered agent
/// receives; untracked build output is out of bounds by design. The four exempt paths are named exactly, never as
/// prefixes, so a new file beside one of them is scanned rather than silently covered.
/// </remarks>
public sealed class RemovedAgentNameRepoGuardTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static string RepoRoot => Corpus.Root;

    /// <summary>The fourteen agents the removal deletes, as <c>RemovedAgentMap</c> lists them.</summary>
    private static readonly string[] RemovedAgentNames =
    [
        "csharp-dev",
        "dal-dev",
        "maui-dev",
        "pulumi-dev",
        "typescript-dev",
        "react-dev",
        "rust-dev",
        "tauri-dev",
        "go-dev",
        "python-dev",
        "sql-database-architect",
        "test-dev",
        "azure-reader",
        "github-devops"
    ];

    /// <summary>
    /// The shipped files that must carry a removed name: the removal map every reader resolves through, the pick
    /// that maps a removed name to its replacement, the shipped technology map a host's agent-keyed task gates keep
    /// resolving through for two minor releases, and the retired-skill map <c>squad doctor</c> reads to warn about
    /// leftover installed copies of a retired skill.
    /// </summary>
    private static readonly string[] ExemptPaths =
    [
        "src/KyberWeave.Application/Docs/Validation/PlanTaskAgentPick.cs",
        "src/KyberWeave.Application/Review/Catalogue/GateCatalogueRenderer.cs",
        "src/KyberWeave.Application/Review/Catalogue/RemovedAgentMap.cs",
        "src/KyberWeave.Application/Squad/Deployment/SquadRetiredSkills.cs"
    ];

    [Fact]
    public void NoShippedSourceFileNamesARemovedAgentOutsideTheMigrationMaps()
    {
        string[] offenders =
        [
            .. TrackedSourceFiles()
                .Where(path => !ExemptPaths.Contains(path, StringComparer.Ordinal))
                .Where(path => RemovedAgentNames.Any(name => NamesWordBounded(Corpus.ReadText(path), name)))
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            offenders.Length == 0,
            "These shipped source files still name an agent the removal deleted. Route them through "
            + "PlanTaskAgentPick.ResolveRemovedAgent or remove the name: " + string.Join(", ", offenders));
    }

    private static IEnumerable<string> TrackedSourceFiles() =>
        TrackedFiles()
            .Where(path => path.StartsWith("src/", StringComparison.Ordinal))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal));

    /// <summary>
    /// The tracked files, listed through <see cref="StdioSession" />: it kills the child tree on dispose whatever
    /// the outcome, and its 60 s budget bounds the wait, so this guard adds no unbounded spawn of its own.
    /// </summary>
    private static List<string> TrackedFiles()
    {
        List<string> tracked = [];
        using StdioSession session = new("git", ["-C", RepoRoot, "ls-files", "-z"], TimeSpan.FromSeconds(60), TimeProvider.System);
        while (true)
        {
            string line;
            try
            {
                line = session.ReadLine();
            }
            catch (EndOfStreamException)
            {
                break;
            }

            // -z separates entries with NUL, so a path with spaces or quotes stays one entry each.
            tracked.AddRange(line
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Replace('\\', '/')));
        }

        return tracked;
    }

    private static bool NamesWordBounded(string text, string name)
    {
        int index = text.IndexOf(name, StringComparison.Ordinal);
        while (index >= 0)
        {
            bool startsWord = index == 0 || !IsNameCharacter(text[index - 1]);
            int after = index + name.Length;
            bool endsWord = after == text.Length || !IsNameCharacter(text[after]);
            if (startsWord && endsWord)
            {
                return true;
            }

            index = text.IndexOf(name, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsNameCharacter(char value) => char.IsLetterOrDigit(value) || value is '-' or '_';
}
