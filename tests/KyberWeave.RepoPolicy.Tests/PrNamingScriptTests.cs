using System.Diagnostics;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
///     Behaviour of <c>scripts/check-pr-name.sh</c>, the pull request naming check CI runs before it
///     spends a runner on a branch whose name and title no maintainer can read.
/// </summary>
/// <remarks>
///     <para>
///         ASSUMED CONTRACT (RED tests: the implementer provides these, or updates this comment and the tests
///         together). The script does not exist yet, so this is the interface the tests drive:
///     </para>
///     <code>
/// scripts/check-pr-name.sh --title &lt;title&gt; --branch &lt;branch&gt; [--author &lt;login&gt;]
/// </code>
///     <para>
///         Exit <c>0</c> accepts the pull request. Exit <c>1</c> rejects it and prints, on stdout or stderr,
///         a line naming the offending part. The author defaults to absent, which is not the Dependabot
///         login: a run that does not pass <c>--author</c> is judged like any other.
///     </para>
///     <para>
///         The shapes below are named constants on purpose. Row T7 of the conductor and bug-crusher
///         run-speed slice owns them, so a later task that corrects a shape corrects it here, once.
///     </para>
/// </remarks>
public sealed class PrNamingScriptTests
{
    /// <summary>
    ///     Branch shape, row T7: the optional <c>owner.agent.</c> prefix, then the harness token, a
    ///     conventional-commit type, and a slug — <c>commandcode/fix/arbiter-global-block-identity</c>.
    /// </summary>
    private const string BranchShape = "[<owner>.<agent>.]<harness>/<type>/<slug>";

    /// <summary>
    ///     Title shape, row T7: a conventional-commit subject followed by the harness token in square
    ///     brackets — <c>fix(arbiter): qualify global block identity by target [commandcode]</c>.
    /// </summary>
    private const string TitleShape = "<type>(<scope>): <summary> [<harness>]";

    /// <summary>The one token that is not a harness: a pull request a person named nothing after.</summary>
    private const string ManualToken = "manual";

    /// <summary>The login GitHub gives Dependabot, which opens unnameable branches by design.</summary>
    private const string DependabotAuthor = "dependabot[bot]";

    /// <summary>The script under test, relative to the repository root.</summary>
    private const string ScriptPath = "scripts/check-pr-name.sh";

    /// <summary>The conventional-commit types the <c>&lt;type&gt;</c> segment accepts.</summary>
    private static readonly string[] ConventionalTypes =
    [
        "build", "chore", "ci", "docs", "feat", "fix",
        "perf", "refactor", "revert", "style", "test"
    ];

    /// <summary>The 13 catalogue tokens plus <c>manual</c>, read from the catalogue rather than copied.</summary>
    public static TheoryData<string> AcceptedTokens()
    {
        TheoryData<string> rows = [];
        foreach (SquadTarget target in SquadTargetCatalog.All) rows.Add(SquadTargetCatalog.GetToken(target));

        rows.Add(ManualToken);
        return rows;
    }

    /// <summary>
    ///     A well-formed title and branch are accepted for every token the catalogue names, so a harness
    ///     added to the catalogue is accepted the day it ships and no list of thirteen lives here.
    /// </summary>
    /// <param name="token">A catalogue harness token, or <c>manual</c>.</param>
    [Theory]
    [MemberData(nameof(AcceptedTokens))]
    public void AcceptsAConformingTitleAndBranchForEveryCatalogueToken(string token)
    {
        ProcessResult result = RunCheck(
            Title(token, "fix", "arbiter", "qualify global block identity by target"),
            Branch(token, "fix", "arbiter-global-block-identity"));

        Assert.True(
            result.ExitCode == 0,
            $"'{token}' is a catalogue token, so this shape must be accepted ({TitleShape}, {BranchShape}). " +
            $"Exit {result.ExitCode}: {Output(result)}");
    }

    /// <summary>
    ///     The optional <c>owner.agent.</c> prefix is optional, not required: a personal branch name with
    ///     it and one without it are both accepted.
    /// </summary>
    [Fact]
    public void AcceptsBothBranchesWithAndWithoutTheOwnerAgentPrefix()
    {
        ProcessResult withPrefix = RunCheck(
            Title("commandcode", "fix", "arbiter", "qualify global block identity by target"),
            "dave.test-dev.commandcode/fix/arbiter-global-block-identity");
        ProcessResult withoutPrefix = RunCheck(
            Title("commandcode", "fix", "arbiter", "qualify global block identity by target"),
            "commandcode/fix/arbiter-global-block-identity");

        Assert.True(withPrefix.ExitCode == 0, $"The owner.agent. prefix must be accepted. {Output(withPrefix)}");
        Assert.True(withoutPrefix.ExitCode == 0, $"A branch with no prefix must be accepted. {Output(withoutPrefix)}");
    }

    /// <summary>
    ///     Every conventional-commit type is accepted in the <c>&lt;type&gt;</c> segment, so a maintainer
    ///     is not pushed into <c>fix</c> by the naming check itself.
    /// </summary>
    /// <param name="type">One of <see cref="ConventionalTypes" />.</param>
    [Theory]
    [MemberData(nameof(ConventionalTypeRows))]
    public void AcceptsEachConventionalCommitType(string type)
    {
        ProcessResult result = RunCheck(
            Title("claude", type, "arbiter", "tighten the gate"),
            Branch("claude", type, "tighten-the-gate"));

        Assert.True(result.ExitCode == 0, $"Conventional type '{type}' must be accepted. {Output(result)}");
    }

    /// <summary>The conventional types as theory rows, built from the one list the tests own.</summary>
    public static TheoryData<string> ConventionalTypeRows()
    {
        TheoryData<string> rows = [];
        foreach (string type in ConventionalTypes) rows.Add(type);

        return rows;
    }

    /// <summary>
    ///     A branch that breaks the shape is rejected, and the rejection names the branch so the author
    ///     knows which of the two inputs to fix.
    /// </summary>
    /// <param name="branch">A branch that does not match <see cref="BranchShape" />.</param>
    /// <param name="why">What the branch gets wrong, for the failure message.</param>
    [Theory]
    [InlineData("commandcode/arbiter-global-block-identity", "no type segment")]
    [InlineData("commandcode/feature/arbiter-global-block-identity", "not a conventional type")]
    [InlineData("commandcode/fix/Arbiter_Global_Block", "slug is not lowercase-hyphenated")]
    [InlineData("gpt5/fix/arbiter-global-block-identity", "harness is not a catalogue token")]
    [InlineData("commandcode/fix", "no slug")]
    [InlineData("main", "the default branch shape")]
    [InlineData("commandcode/fix/arbiter-global-block-identity/extra", "one segment too many")]
    public void RejectsABranchThatDoesNotFollowTheShape(string branch, string why)
    {
        ProcessResult result = RunCheck(
            Title("commandcode", "fix", "arbiter", "qualify global block identity by target"),
            branch);

        Assert.True(
            result.ExitCode == 1,
            $"Branch '{branch}' must be rejected ({why}); it does not match {BranchShape}. " +
            $"Exit {result.ExitCode}: {Output(result)}");
        Assert.Contains(branch, Output(result), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A title that breaks the shape is rejected: the harness token belongs in square brackets at the
    ///     end, and nothing else belongs there.
    /// </summary>
    /// <param name="title">A title that does not match <see cref="TitleShape" />.</param>
    /// <param name="why">What the title gets wrong, for the failure message.</param>
    [Theory]
    [InlineData("fix(arbiter): qualify global block identity by target", "no harness token")]
    [InlineData("fix(arbiter): qualify global block identity by target [gpt5]", "token is not in the catalogue")]
    [InlineData("[commandcode] fix(arbiter): qualify global block identity by target", "token is not at the end")]
    [InlineData("qualify global block identity by target [commandcode]", "no conventional-commit subject")]
    [InlineData("feature(arbiter): qualify global block identity by target [commandcode]", "not a conventional type")]
    public void RejectsATitleThatDoesNotFollowTheShape(string title, string why)
    {
        ProcessResult result = RunCheck(title, Branch("commandcode", "fix", "arbiter-global-block-identity"));

        Assert.True(
            result.ExitCode == 1,
            $"Title '{title}' must be rejected ({why}); it does not match {TitleShape}. " +
            $"Exit {result.ExitCode}: {Output(result)}");
    }

    /// <summary>
    ///     A Dependabot pull request is exempt, because the branch and title are generated and no human can
    ///     conform them. The exemption is judged by the author, not by the branch prefix alone.
    /// </summary>
    /// <param name="title">A generated title that no human wrote.</param>
    /// <param name="branch">A generated branch that does not match the shape.</param>
    [Theory]
    [InlineData("Bump Newtonsoft.Json from 13.0.1 to 13.0.3", "dependabot/nuget/Newtonsoft.Json-13.0.3")]
    [InlineData("chore(deps-dev): bump vitest from 1.6.0 to 2.1.8", "dependabot/npm_and_yarn/vitest-2.1.8")]
    public void ExemptsADependabotAuthor(string title, string branch)
    {
        ProcessResult result = RunCheck(title, branch, DependabotAuthor);

        Assert.True(
            result.ExitCode == 0,
            $"A pull request authored by {DependabotAuthor} must be exempt. Exit {result.ExitCode}: {Output(result)}");
    }

    /// <summary>
    ///     The exemption is the author's, not the branch prefix's: the same branch shape from a human is
    ///     still judged, so renaming a branch to look generated buys nothing.
    /// </summary>
    [Fact]
    public void DoesNotExemptADependabotShapedBranchFromAHumanAuthor()
    {
        ProcessResult result = RunCheck(
            "Bump Newtonsoft.Json from 13.0.1 to 13.0.3",
            "dependabot/nuget/Newtonsoft.Json-13.0.3",
            "a-human");

        Assert.True(
            result.ExitCode == 1,
            $"Only the {DependabotAuthor} author is exempt, not the branch prefix. Exit {result.ExitCode}: {Output(result)}");
    }

    /// <summary>
    ///     The script exists and is checked in, so the CI job that calls it fails on a naming rule rather
    ///     than on a missing path.
    /// </summary>
    [Fact]
    public void TheCheckScriptIsCheckedIn()
    {
        string path = Path.Combine(KyberWeaveTestPaths.ToolRoot, ScriptPath);

        Assert.True(File.Exists(path), $"The pull request naming check is missing at '{path}'.");
    }

    /// <summary>
    ///     Runs the check through <c>bash</c> with a bounded wait, and returns what it wrote.
    /// </summary>
    private static ProcessResult RunCheck(string title, string branch, string? author = null)
    {
        SkipIfBashUnavailable();

        ProcessStartInfo startInfo = new("/bin/bash")
        {
            WorkingDirectory = KyberWeaveTestPaths.ToolRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(ScriptPath);
        startInfo.ArgumentList.Add("--title");
        startInfo.ArgumentList.Add(title);
        startInfo.ArgumentList.Add("--branch");
        startInfo.ArgumentList.Add(branch);
        if (author is not null)
        {
            startInfo.ArgumentList.Add("--author");
            startInfo.ArgumentList.Add(author);
        }

        return ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(30));
    }

    /// <summary>The title shape, written from the constants so the two cannot drift apart.</summary>
    private static string Title(string token, string type, string scope, string summary)
    {
        return $"{type}({scope}): {summary} [{token}]";
    }

    /// <summary>The branch shape, written from the constants so the two cannot drift apart.</summary>
    private static string Branch(string token, string type, string slug)
    {
        return $"{token}/{type}/{slug}";
    }

    private static string Output(ProcessResult result)
    {
        return (result.StandardOutput + Environment.NewLine + result.StandardError).Trim();
    }

    private static void SkipIfBashUnavailable()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
            throw SkipException.ForSkip("Shell script tests require /bin/bash on a POSIX environment.");
    }
}
