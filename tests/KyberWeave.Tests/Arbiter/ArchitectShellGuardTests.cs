using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for the architect shell guard (<c>KW-ARB-SHELL-001</c>). The architect may run only the
/// commands its agent file documents, and the guard denies every other <c>Bash</c> or
/// <c>PowerShell</c> call before it runs. A call is allowed only when every simple command in it is
/// on the list: a separator, a substitution, a redirect to a file, a subshell, a background job or an
/// unlisted flag each make the whole call a denial. The planning directories are the ones
/// <see cref="ReadGuard.ProtectedDirectories"/> resolves for this repository, so <c>touch</c> is
/// allowed only beneath them.
/// RED: <see cref="ArchitectShellGuard"/> does not exist yet.
/// </summary>
public sealed class ArchitectShellGuardTests
{
    private const string Worktree = "/w";

    private static readonly Lazy<IReadOnlyList<string>> PlanningDirectories = new(() =>
        ReadGuard.ProtectedDirectories(KyberWeaveConfigLoader.Load(KyberWeaveTestPaths.ToolRoot)));

    private const string RuleId = ArchitectShellGuard.RuleId;

    private static JsonElement CommandInput(string command) =>
        JsonSerializer.SerializeToElement(new { command });

    private static ShellGuardResult CheckBash(string command) =>
        ArchitectShellGuard.Check("Bash", CommandInput(command), Worktree, PlanningDirectories.Value);

    private static ShellGuardResult Check(string tool, string command) =>
        ArchitectShellGuard.Check(tool, CommandInput(command), Worktree, PlanningDirectories.Value);

    /// <summary>Every documented form whose sample the architect may run, fed to the guard as a Bash call.</summary>
    public static IEnumerable<object[]> AllowedDocumentedSamples() =>
        ArchitectShellGuard.DocumentedForms.Values
            .Where(form => form.Allowed)
            .Select(form => new object[] { form.SampleCommand });

    [Theory]
    [MemberData(nameof(AllowedDocumentedSamples))]
    public void Bash_DocumentedAllowedSample_IsAllowed(string sample)
    {
        ShellGuardResult result = CheckBash(sample);

        Assert.True(result.Allowed, $"'{sample}' is a documented form and must be allowed. Reason: {result.Reason}");
    }

    [Theory]
    [InlineData("cd /w && kyber-weave docs validate . --merge-ready")]
    [InlineData("kyber-weave docs validate /w --merge-ready")]
    [InlineData("kyber-weave docs drift .")]
    [InlineData("codegraph explore \"ArbiterHookWiring BuildHooks\"")]
    [InlineData("codegraph explore 'a && b'")]
    [InlineData("kyber-weave doctor --ensure")]
    [InlineData("git status")]
    [InlineData("git -C /w log --oneline -5")]
    [InlineData("git --no-pager diff main...HEAD")]
    [InlineData("git show HEAD:path")]
    [InlineData("git rev-parse --git-common-dir")]
    [InlineData("git log -3 2>&1")]
    [InlineData("grep -rn \"Foo\" src")]
    [InlineData("rg -n Foo src")]
    [InlineData("ls docs/plans")]
    [InlineData("find docs -name '*.md'")]
    [InlineData("git log --oneline | head -5")]
    [InlineData("grep -c x f | sort | uniq")]
    [InlineData("touch /w/docs/plans/2026-11-12-x.md")]
    [InlineData("cd /w && touch docs/plans/x.tasks.json")]
    [InlineData("sort f")]
    [InlineData("sort -r f")]
    [InlineData("tail -5 f")]
    [InlineData("kyber-weave arbiter plan docs/plans/x.md -f json")]
    [InlineData("cd /w && kyber-weave arbiter plan docs/plans/x.md -f json")]
    public void Bash_ListedCommand_IsAllowed(string command)
    {
        ShellGuardResult result = CheckBash(command);

        Assert.True(result.Allowed, $"'{command}' is on the architect's list and must be allowed. Reason: {result.Reason}");
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("cd C:\\w; git log -1")]
    public void PowerShell_ListedCommand_IsAllowed(string command)
    {
        ShellGuardResult result = Check("PowerShell", command);

        Assert.True(result.Allowed, $"'{command}' is on the architect's list and must be allowed. Reason: {result.Reason}");
    }

    [Theory]
    [InlineData("sed -n 1,5p f", "sed -n 1,5p f")]
    [InlineData("awk '{print}' f", "awk '{print}' f")]
    [InlineData("cat f", "cat f")]
    [InlineData("gh issue view 599", "gh issue view 599")]
    [InlineData("curl x", "curl x")]
    [InlineData("python3 -c 'print(1)'", "python3 -c 'print(1)'")]
    [InlineData("python3 scratch.py", "python3 scratch.py")]
    [InlineData("for f in a b; do git log $f; done", "for f in a b")]
    [InlineData("git status; python x.py", "python x.py")]
    [InlineData("git log $(cat f)", "git log $(cat f)")]
    [InlineData("git log `x`", "git log `x`")]
    [InlineData("codegraph explore \"$(rm -rf x)\"", "codegraph explore \"$(rm -rf x)\"")]
    [InlineData("git log > out.txt", "git log > out.txt")]
    [InlineData("git log >> f", "git log >> f")]
    [InlineData("cat <<EOF", "cat <<EOF")]
    [InlineData("(git status)", "(git status)")]
    [InlineData("git status &", "git status &")]
    [InlineData("FOO=1 git status", "FOO=1 git status")]
    [InlineData("bash -c 'git status'", "bash -c 'git status'")]
    [InlineData("xargs ls", "xargs ls")]
    [InlineData("git -c core.pager=x log", "git -c core.pager=x log")]
    [InlineData("git diff --output=f", "git diff --output=f")]
    [InlineData("git checkout main", "git checkout main")]
    [InlineData("git commit -m x", "git commit -m x")]
    [InlineData("git fetch", "git fetch")]
    [InlineData("git grep x", "git grep x")]
    [InlineData("find . -exec rm {} \\;", "find . -exec rm {} \\;")]
    [InlineData("find . -delete", "find . -delete")]
    [InlineData("rg --pre cat x", "rg --pre cat x")]
    [InlineData("sort -o f x", "sort -o f x")]
    [InlineData("uniq a b", "uniq a b")]
    [InlineData("tee f", "tee f")]
    [InlineData("echo x", "echo x")]
    [InlineData("touch src/x.cs", "touch src/x.cs")]
    [InlineData("touch /w/docs/plans/../../src/x.md", "touch /w/docs/plans/../../src/x.md")]
    [InlineData("touch docs/plans/*.md", "touch docs/plans/*.md")]
    [InlineData("touch -r a docs/plans/x.md", "touch -r a docs/plans/x.md")]
    [InlineData("cd -", "cd -")]
    [InlineData("git log \"x", "git log \"x")]
    [InlineData("sort --ou=out1.txt in.txt", "sort --ou=out1.txt in.txt")]
    [InlineData("sort --out=f x", "sort --out=f x")]
    [InlineData("sort --comp=gzip x", "sort --comp=gzip x")]
    [InlineData("sort -ofile x", "sort -ofile x")]
    [InlineData("rg --hostname-bin=/tmp/x --hyperlink-format=default --color=always p", "rg --hostname-bin=/tmp/x --hyperlink-format=default --color=always p")]
    [InlineData("rg --hyperlink-format=default p", "rg --hyperlink-format=default p")]
    [InlineData("tail -f f", "tail -f f")]
    [InlineData("tail -F f", "tail -F f")]
    [InlineData("tail --follow f", "tail --follow f")]
    [InlineData("touch ../other/docs/plans/x.md", "touch ../other/docs/plans/x.md")]
    [InlineData("touch ~/docs/plans/x.md", "touch ~/docs/plans/x.md")]
    [InlineData("kyber-weave arbiter plan docs/plans/x.md", "kyber-weave arbiter plan docs/plans/x.md")]
    [InlineData("kyber-weave arbiter eval -f json", "kyber-weave arbiter eval -f json")]
    [InlineData("kyber-weave arbiter install", "kyber-weave arbiter install")]
    [InlineData("kyber-weave arbiter plan a b -f json", "kyber-weave arbiter plan a b -f json")]
    public void Bash_CommandOutsideTheList_IsDeniedNamingTheOffendingSegment(string command, string offendingSegment)
    {
        ShellGuardResult result = CheckBash(command);

        Assert.False(result.Allowed, $"'{command}' is outside the architect's list and must be denied.");
        string reason = result.Reason ?? string.Empty;
        Assert.Contains(RuleId, reason, StringComparison.Ordinal);
        Assert.Contains(offendingSegment, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Bash_EmptyCommand_IsDeniedUnderTheRule()
    {
        ShellGuardResult result = CheckBash(string.Empty);

        Assert.False(result.Allowed, "An empty command is not on the architect's list and must be denied.");
        Assert.Contains(RuleId, result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("git log $(x)", "git log $(x)")]
    [InlineData("git log `n", "git log `n")]
    [InlineData("& git status", "& git status")]
    [InlineData("Set-Content f x", "Set-Content f x")]
    [InlineData("git status 2>$null", "git status 2>$null")]
    public void PowerShell_CommandOutsideTheList_IsDeniedNamingTheOffendingSegment(string command, string offendingSegment)
    {
        ShellGuardResult result = Check("PowerShell", command);

        Assert.False(result.Allowed, $"'{command}' is outside the architect's list and must be denied.");
        string reason = result.Reason ?? string.Empty;
        Assert.Contains(RuleId, reason, StringComparison.Ordinal);
        Assert.Contains(offendingSegment, reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"command\": 5}")]
    [InlineData("{\"command\": null}")]
    public void Bash_MissingOrNonStringCommand_IsDenied(string toolInputJson)
    {
        using JsonDocument document = JsonDocument.Parse(toolInputJson);

        ShellGuardResult result = ArchitectShellGuard.Check(
            "Bash",
            document.RootElement.Clone(),
            Worktree,
            PlanningDirectories.Value);

        Assert.False(result.Allowed, $"A tool input of {toolInputJson} carries no command string and must be denied.");
        Assert.Contains(RuleId, result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void IsShellGuarded_TheArchitect_IsGuarded()
    {
        Assert.True(ArchitectShellGuard.IsShellGuarded("architect"));
    }

    [Theory]
    [InlineData("csharp-dev")]
    [InlineData("conductor")]
    public void IsShellGuarded_OtherAgents_AreNotGuarded(string agent)
    {
        Assert.False(ArchitectShellGuard.IsShellGuarded(agent));
    }

    [Fact]
    public void IsShellGuarded_NoAgent_IsNotGuarded()
    {
        Assert.False(ArchitectShellGuard.IsShellGuarded(null));
    }
}
