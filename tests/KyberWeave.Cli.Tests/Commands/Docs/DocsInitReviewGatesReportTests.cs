using System.Text.RegularExpressions;
using KyberWeave.Core.Review.Catalogue;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Cli.Tests.Commands.Docs;

/// <summary>
///     What <c>docs init</c> reports about the review gates it writes, leaves alone or skips: the opt-out, a stack with
///     no shipped gates, the families a go-only host comments out, the per-technology listing and the missing-gate
///     information on an existing block. The command runs over an in-memory checkout; the written block itself is
///     proven by the scaffolder's own tests.
/// </summary>
public sealed class DocsInitReviewGatesReportTests
{
    private const string ConfigRelativePath = ".kyber-weave/kyber-weave.yml";
    private const string OptOutLine = "review gates not written (--no-review-gates)";

    private const string CiTemplateLine =
        "CI: copy templates/github-actions/kyber-weave-review-gate.yml from " +
        "https://github.com/dpalfery/kyber-weave into .github/workflows/.";

    private readonly MemoryDocsHost _host = new();

    private int RunDocsInit(string[] arguments) =>
        CommandAppFixture.DocsInit(_host.Register, _host.Console).Run(arguments);

    /// <summary>
    ///     The opt-out writes no gates and says so. An operator who manages review gates by
    ///     hand must not find a generated block after asking for none.
    /// </summary>
    [Fact]
    public void NoReviewGatesWritesNothingAndSaysSo()
    {
        NewFixtureRepo("go", ["go"]);

        int exit = RunDocsInit(["docs", "init", _host.Repo, "--no-skill", "--no-review-gates"]);

        Assert.True(
            exit == 0,
            $"docs init --no-review-gates exited {exit}. Output:{Environment.NewLine}{_host.Output}");
        Assert.Contains(OptOutLine, _host.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, ReadReviewSection());
    }

    /// <summary>
    ///     A declared technology with no catalogue entry is reported, not failed. A stack
    ///     without shipped gates is a valid state, and failing on it would block adoption.
    /// </summary>
    [Fact]
    public void AHostWithOnlyUnfamiliedTechnologiesWritesNoReviewSection()
    {
        DocsInitFixtureSeed.HostConfig(_host.Files, _host.Repo, ["azure"]);

        int exit = RunDocsInit(["docs", "init", _host.Repo, "--no-skill"]);

        Assert.True(
            exit == 0,
            $"docs init on an azure-only host exited {exit}. Output:{Environment.NewLine}{_host.Output}");
        Assert.Contains("no gates shipped for azure", _host.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, ReadReviewSection());
    }

    /// <summary>
    ///     Merged <c>--kyber-standards</c> technologies render the families whose roots exist
    ///     live and comment the rest out with the marker the host should point at. The go-only
    ///     fixture has exactly one root, so every other family must explain itself.
    /// </summary>
    [Fact]
    public void KyberStandardsOnAGoOnlyFixtureWritesGoGatesAndCommentsTheOtherFamiliesOut()
    {
        NewFixtureRepo("go", ["go"]);

        int exit = RunDocsInit(["docs", "init", _host.Repo, "--no-skill", "--kyber-standards"]);

        Assert.True(
            exit == 0,
            $"docs init --kyber-standards exited {exit}. Output:{Environment.NewLine}{_host.Output}");
        IReadOnlyList<GateCatalogueFamily> families = GateCatalogueRenderer.LoadEmbedded();
        GateCatalogueFamily go =
            families.Single(candidate => candidate.Technologies.Contains("go", StringComparer.Ordinal));
        string configText = _host.Files.ReadAllText(_host.InRepo(ConfigRelativePath));
        Assert.Contains(
            configText.Split('\n'),
            line => line.TrimStart().StartsWith("- id: go-", StringComparison.Ordinal));
        foreach (GateCatalogueFamily other in families)
        {
            if (string.Equals(other.Family, go.Family, StringComparison.Ordinal)) continue;

            string reason = $"no {other.Markers[0]} found";
            Assert.Contains(reason, configText, StringComparison.Ordinal);
            Assert.Contains(reason, _host.Output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     Writing gates is reported per technology — the ids written, the gates commented
    ///     out with their reasons — and points at the CI template. The exact ids are pinned
    ///     by the approved-files test; this pins the reporting contract.
    /// </summary>
    [Fact]
    public void DocsInitListsWrittenAndCommentedOutGatesPerTechnology()
    {
        NewFixtureRepo("go", ["go"]);

        int exit = RunDocsInit(["docs", "init", _host.Repo, "--no-skill"]);

        Assert.True(
            exit == 0,
            $"docs init exited {exit}. Output:{Environment.NewLine}{_host.Output}");
        Assert.Contains(CiTemplateLine, _host.Output, StringComparison.Ordinal);
        Assert.Matches(@"\bgo-[a-z0-9-]+\b", _host.Output);
        Assert.False(
            string.IsNullOrEmpty(ReadReviewSection()),
            "docs init wrote no review: section, so there was nothing to list.");
    }

    /// <summary>
    ///     Leaving an existing block in place still tells the operator which catalogue gates
    ///     are missing from it, without changing the exit code. Silence here would let a host
    ///     drift behind the catalogue without noticing.
    /// </summary>
    [Fact]
    public void AnExistingReviewSectionListsMissingCatalogueGatesAsInformation()
    {
        NewFixtureRepo("go", ["go"]);
        _host.WriteInRepo(ConfigRelativePath, HandOwnedConfigWithReview());

        int exit = RunDocsInit(["docs", "init", _host.Repo, "--no-skill"]);

        Assert.True(
            exit == 0,
            $"docs init exited {exit}. Output:{Environment.NewLine}{_host.Output}");
        Assert.Contains("go-vet", _host.Output, StringComparison.Ordinal);
    }

    private void NewFixtureRepo(string family, IReadOnlyList<string> technologies) =>
        DocsInitFixtureSeed.Family(_host.Files, _host.Repo, family, technologies);

    /// <summary>
    ///     The written <c>review:</c> section: from the top-level <c>review</c> key (a commented
    ///     <c># review:</c> does not count) to the end of the file. Empty when absent.
    /// </summary>
    private string ReadReviewSection()
    {
        string text = _host.Files.ReadAllText(_host.InRepo(ConfigRelativePath)).ReplaceLineEndings("\n");
        string[] lines = (text.EndsWith('\n') ? text[..^1] : text).Split('\n');
        for (int index = 0; index < lines.Length; index++)
            if (Regex.IsMatch(lines[index], "^review[ \\t]*:"))
                return string.Join("\n", lines, index, lines.Length - index) + "\n";

        return string.Empty;
    }

    /// <summary>
    ///     A hand-owned host configuration: operator comments, the go stack, and a review
    ///     block with ids the catalogue never emits, so every catalogue gate for go is missing
    ///     from it.
    /// </summary>
    private static string HandOwnedConfigWithReview()
    {
        return """
               # Ours, not the tool's.
               ontology:
                 docs-root: docs
                 technologies:
                   - go
               review:
                 gates:
                   - id: hand-owned-council
                     run: [git, --version]
                 task-gates:
                   go-dev:
                     - id: hand-owned-task
                       include: ["**/*.go"]
                       run: [git, --version]
               """;
    }
}
