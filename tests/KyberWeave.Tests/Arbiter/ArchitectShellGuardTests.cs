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
/// Pinned against the shipped <see cref="ArchitectShellGuard"/>: each denial and allowance below is a regression
/// the guard must keep, and the structural and chained-cd cases were red before their fixes landed.
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
    [InlineData("git log --oneline -5")]
    [InlineData("git diff --stat --name-only main...HEAD")]
    [InlineData("rg -n --glob '*.md' Foo docs")]
    [InlineData("rg --files-with-matches Foo docs")]
    [InlineData("cd /w && touch docs/plans/x.md")]
    [InlineData("touch docs/./plans/x.md")]
    [InlineData("codegraph explore \"x\"")]
    [InlineData("ls docs 2>/dev/null")]
    [InlineData("git diff --output-indicator-new=+ --output-indicator-old=-")]
    [InlineData("rg --pretty p docs")]
    [InlineData("codegraph explore \"-x\"")]
    [InlineData("codegraph explore \"x y\"")]
    [InlineData("git status || git log")]
    [InlineData("uniq -f 1 file")]
    [InlineData("uniq -s 2 file")]
    [InlineData("uniq -w 3 file")]
    [InlineData("uniq file")]
    [InlineData("grep -rn \"a b\" src")]
    [InlineData("rg -n '#include' src")]
    [InlineData("grep -c x#y f")]
    [InlineData("rg --glob '*.md' x docs")]
    [InlineData("rg -n 'a|b' src")]
    [InlineData("grep -rn 'a*b' src")]
    [InlineData("sort -k 2 f")]
    [InlineData("uniq --skip-fields 1 f")]
    [InlineData("rg 'foo\\.bar' src")]
    [InlineData("git status\n")]
    public void Bash_ListedCommand_IsAllowed(string command)
    {
        ShellGuardResult result = CheckBash(command);

        Assert.True(result.Allowed, $"'{command}' is on the architect's list and must be allowed. Reason: {result.Reason}");
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("cd C:\\w; git log -1")]
    [InlineData("git status; git log -1")]
    [InlineData("git status 2>&1")]
    [InlineData("git status || git log")]
    [InlineData("ls docs\\plans")]
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
    [InlineData("tail --fo f", "tail --fo f")]
    [InlineData("tail -nf 5 f", "tail -nf 5 f")]
    [InlineData("rg -nz p", "rg -nz p")]
    [InlineData("touch \"docs/plans/*.md\"", "touch \"docs/plans/*.md\"")]
    [InlineData("uniq -- in -out", "uniq -- in -out")]
    [InlineData("git log | uniq -- - -b", "uniq -- - -b")]
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
    [InlineData("rg --hostname-bin=x p", "rg --hostname-bin=x p")]
    [InlineData("rg --hyperlink-format=x p", "rg --hyperlink-format=x p")]
    [InlineData("find . -fls f", "find . -fls f")]
    [InlineData("find . -fprintf f %p", "find . -fprintf f %p")]
    [InlineData("find . -fprint0 f", "find . -fprint0 f")]
    [InlineData("git --exec-path=x log", "git --exec-path=x log")]
    [InlineData("git --config-env=a=b log", "git --config-env=a=b log")]
    [InlineData("git --namespace=x log", "git --namespace=x log")]
    [InlineData("git -C --no-pager log", "git -C --no-pager log")]
    [InlineData("touch docs/plans/a.md src/b.md", "touch docs/plans/a.md src/b.md")]
    [InlineData("touch src/a.md docs/plans/b.md", "touch src/a.md docs/plans/b.md")]
    [InlineData("touch docs/plans/x.txt", "touch docs/plans/x.txt")]
    [InlineData("touch /abs/elsewhere/docs/plans/x.md", "touch /abs/elsewhere/docs/plans/x.md")]
    [InlineData("codegraph explore \"\"", "codegraph explore \"\"")]
    [InlineData("codegraph explore -x", "codegraph explore -x")]
    [InlineData("kyber-weave docs validate -x --merge-ready", "kyber-weave docs validate -x --merge-ready")]
    [InlineData("cd /w", "cd /w")]
    [InlineData("cd", "cd")]
    [InlineData("cd /tmp && touch docs/plans/x.md", "cd /tmp")]
    [InlineData("git diff --out=f", "git diff --out=f")]
    [InlineData("git diff --ou=f", "git diff --ou=f")]
    [InlineData("git diff --o=f", "git diff --o=f")]
    [InlineData("git diff --ext-diff", "git diff --ext-diff")]
    [InlineData("git diff --textconv", "git diff --textconv")]
    [InlineData("git log --ext=x", "git log --ext=x")]
    [InlineData("rg --hos=x p", "rg --hos=x p")]
    [InlineData("rg --hyp=x p", "rg --hyp=x p")]
    [InlineData("rg --pr=cat p", "rg --pr=cat p")]
    [InlineData("rg --search p", "rg --search p")]
    [InlineData("rg --pre-g=x p", "rg --pre-g=x p")]
    [InlineData("codegraph explore \"a\" b", "codegraph explore \"a\" b")]
    [InlineData("codegraph explore --x \"a\"", "codegraph explore --x \"a\"")]
    [InlineData("kyber-weave doctor --ensure /etc/passwd", "kyber-weave doctor --ensure /etc/passwd")]
    [InlineData("find . -execdir ls {} +", "find . -execdir ls {} +")]
    [InlineData("find . -okdir ls {} ;", "find . -okdir ls {} ;")]
    [InlineData("find . -fprint f", "find . -fprint f")]
    [InlineData("rg --pre-glob x p", "rg --pre-glob x p")]
    [InlineData("rg -z p", "rg -z p")]
    [InlineData("git --git-dir=x log", "git --git-dir=x log")]
    [InlineData("git --work-tree=x log", "git --work-tree=x log")]
    [InlineData("sh -c x", "sh -c x")]
    [InlineData("sudo ls", "sudo ls")]
    [InlineData("env ls", "env ls")]
    [InlineData("node x", "node x")]
    [InlineData("ls $HOME", "ls $HOME")]
    [InlineData("ls ${HOME}", "ls ${HOME}")]
    public void Bash_CommandOutsideTheList_IsDeniedNamingTheOffendingSegment(string command, string offendingSegment)
    {
        ShellGuardResult result = CheckBash(command);

        Assert.False(result.Allowed, $"'{command}' is outside the architect's list and must be denied.");
        string reason = result.Reason ?? string.Empty;
        Assert.Contains(RuleId, reason, StringComparison.Ordinal);
        Assert.Contains(offendingSegment, reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Structural hazards the pre-filter refuses before any segment is judged: a comment, a line feed inside a
    /// quoted word, a backslash or line feed that a quoted word escapes, a non-ASCII or control character used as
    /// a separator or inside a word, and a cd whose directory is not tracked followed by a touch. Each is denied
    /// under the rule id, whatever the rest of the command would have allowed.
    /// </summary>
    [Theory]
    [InlineData("Bash", "ls #'\nrm -rf x\n#'")]
    [InlineData("Bash", "ls # comment")]
    [InlineData("Bash", "find d \"\\\n-delete\"")]
    [InlineData("Bash", "sort \"\\\n-o\"")]
    [InlineData("Bash", "git log \"--out\\\nput=f\"")]
    [InlineData("Bash", "touch \"docs/plans/\\\n../x.md\"")]
    [InlineData("Bash", "ls \"a\nb\"")]
    [InlineData("Bash", "cd docs/plans/nope && git status || touch ../x.md")]
    [InlineData("Bash", "cd docs/plans/nope; touch x.md")]
    [InlineData("Bash", "ls .")]
    [InlineData("Bash", "ls​.")]
    [InlineData("Bash", "ls﻿.")]
    [InlineData("Bash", "ls\u0000.")]
    [InlineData("Bash", "ls\u000B.")]
    [InlineData("Bash", "ls\u000C.")]
    [InlineData("Bash", "ls a\\b")]
    [InlineData("PowerShell", "rg x  --pre=cat")]
    [InlineData("PowerShell", "find .  -delete")]
    [InlineData("PowerShell", "ls a\u000Bb")]
    [InlineData("PowerShell", "ls a\u000Cb")]
    [InlineData("PowerShell", "ls a b")]
    [InlineData("PowerShell", "cd docs/plans/nope; touch x.md")]
    [InlineData("PowerShell", "# comment")]
    public void CommandWithAStructuralHazard_IsDeniedUnderTheRule(string tool, string command)
    {
        ShellGuardResult result = Check(tool, command);

        Assert.False(result.Allowed, $"'{command}' ({tool}) carries a structural hazard and must be denied.");
        Assert.Contains(RuleId, result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell_CdAndTouchUnderADriveWorkingDirectoryWithBackslashes_IsAllowed()
    {
        // PowerShell keeps the backslash literal, so the drive path and the cd target both resolve as written.
        ShellGuardResult result = ArchitectShellGuard.Check(
            "PowerShell",
            CommandInput("cd C:\\w; touch docs/plans/x.md"),
            "C:\\w",
            PlanningDirectories.Value);

        Assert.True(result.Allowed, $"A planning-folder touch after cd C:\\w under a C:\\w working directory must be allowed. Reason: {result.Reason}");
    }

    /// <summary>
    /// The find actions that run a program or write a file. Each row reaches the action check, not the brace or
    /// backslash check, so the reason must name the action itself.
    /// </summary>
    [Theory]
    [InlineData("find . -exec rm x ;", "-exec")]
    [InlineData("find . -exec rm x +", "-exec")]
    [InlineData("find . -execdir rm x ;", "-execdir")]
    [InlineData("find . -ok rm x ;", "-ok")]
    [InlineData("find . -okdir rm x ;", "-okdir")]
    [InlineData("find . -exec rm '{}' ;", "-exec")]
    public void FindExecAction_IsDeniedNamingTheAction(string command, string action)
    {
        ShellGuardResult result = CheckBash(command);

        Assert.False(result.Allowed, $"'{command}' runs a program through find and must be denied.");
        string reason = result.Reason ?? string.Empty;
        Assert.Contains(RuleId, reason, StringComparison.Ordinal);
        Assert.Contains(action, reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cd whose directory the touch would not resolve beneath the planning folder from the worktree, and a cd
    /// reached only after a separator other than &amp;&amp;. The reason names the cd, not just the rule.
    /// </summary>
    [Theory]
    [InlineData("cd /w/src && touch docs/plans/x.md", "touch docs/plans/x.md")]
    [InlineData("git status || cd docs && ls", "cd docs")]
    [InlineData("git status | cd docs && ls", "cd docs")]
    public void CdOrTouchInATrackedOrChainedCall_IsDeniedNamingTheSegment(string command, string offendingSegment)
    {
        ShellGuardResult result = CheckBash(command);

        Assert.False(result.Allowed, $"'{command}' must be denied.");
        string reason = result.Reason ?? string.Empty;
        Assert.Contains(RuleId, reason, StringComparison.Ordinal);
        Assert.Contains(offendingSegment, reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A call with more than one cd segment, and an unquoted glob in an operand. A cd chain leaves the directory
    /// the later touch resolves against ambiguous, and an unquoted glob expands before the list judges it. Each is
    /// denied under the rule id, pinned without a fragment because the reason names the rule.
    /// </summary>
    [Theory]
    [InlineData("PowerShell", "cd nope; cd ..; touch docs/plans/x.md")]
    [InlineData("Bash", "cd a && cd b && ls")]
    [InlineData("Bash", "cd /w && cd docs && touch plans/x.md")]
    [InlineData("Bash", "ls docs/*.md")]
    [InlineData("Bash", "grep -rn x docs/p?ans")]
    [InlineData("Bash", "head -5 docs/[a]*.md")]
    [InlineData("Bash", "git status ;; git log")]
    [InlineData("Bash", "wc -l *")]
    public void CommandWithAChainedCdOrUnquotedGlob_IsDeniedUnderTheRule(string tool, string command)
    {
        ShellGuardResult result = Check(tool, command);

        Assert.False(result.Allowed, $"'{command}' ({tool}) must be denied.");
        Assert.Contains(RuleId, result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Bash_TrailingSeparator_IsAllowed()
    {
        // A trailing ';' ends the list with no empty command after it, so the guard lets the command through.
        ShellGuardResult result = CheckBash("git status;");

        Assert.True(result.Allowed, $"A trailing ';' after a listed command must be allowed. Reason: {result.Reason}");
    }

    /// <summary>
    /// Commands denied for a reason that is not a single offending segment: an unquoted control or Unicode
    /// separator the tokenizer must not read as a word, a cd whose directory is not tracked, or a cd with no
    /// chained command. These pin the denial and the rule id only.
    /// </summary>
    [Theory]
    [InlineData("Bash", "ls .\rrm x")]
    [InlineData("Bash", "ls . || cd docs/plans && touch x.json")]
    [InlineData("Bash", "ls | cd docs/plans && touch x.json")]
    [InlineData("Bash", "cd ~ && touch docs/plans/x.md")]
    [InlineData("Bash", "cd ~/docs && ls")]
    [InlineData("Bash", "cd /w; touch docs/plans/x.md")]
    [InlineData("PowerShell", "ls .\rRemove-Item x")]
    [InlineData("PowerShell", "ls 'a\u2019; Remove-Item x \u2018b'")]
    [InlineData("PowerShell", "ls a\u2018b")]
    [InlineData("PowerShell", "ls a\u2019b")]
    [InlineData("PowerShell", "ls a\u201Ab")]
    [InlineData("PowerShell", "ls a\u201Bb")]
    [InlineData("PowerShell", "ls a\u201Cb")]
    [InlineData("PowerShell", "ls a\u201Db")]
    [InlineData("PowerShell", "ls a\u201Eb")]
    [InlineData("PowerShell", "ls x\u0085Remove-Item y")]
    [InlineData("PowerShell", "ls x\u2028Remove-Item y")]
    [InlineData("PowerShell", "ls x\u2029Remove-Item y")]
    [InlineData("PowerShell", "ls . || cd docs/plans; touch x.json")]
    public void CommandWithAnUntrackedSeparatorOrDirectory_IsDeniedUnderTheRule(string tool, string command)
    {
        ShellGuardResult result = Check(tool, command);

        Assert.False(result.Allowed, $"'{command}' ({tool}) must be denied.");
        Assert.Contains(RuleId, result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Bash_TouchPlanFileUnderAGitBashStyleWorkingDirectory_IsAllowed()
    {
        // Git Bash reports the worktree as /c/w; the same planning-folder touch resolves beneath it.
        ShellGuardResult result = ArchitectShellGuard.Check(
            "Bash",
            CommandInput("touch docs/plans/x.md"),
            "/c/w",
            PlanningDirectories.Value);

        Assert.True(result.Allowed, $"A planning-folder touch under /c/w must be allowed. Reason: {result.Reason}");
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
    [InlineData("git status 2>/dev/null", "git status 2>/dev/null")]
    [InlineData("cd C:\\w; touch docs/plans/x.md", "C:\\w")]
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
        Assert.True(ArchitectShellGuard.IsShellGuarded("Architect"));
    }

    [Fact]
    public void Bash_TouchPlanFileWithAnEmptyWorkingDirectory_IsDenied()
    {
        // With no working directory the guard cannot resolve the touch target beneath a planning folder.
        ShellGuardResult result = ArchitectShellGuard.Check(
            "Bash",
            CommandInput("touch docs/plans/x.md"),
            string.Empty,
            PlanningDirectories.Value);

        Assert.False(result.Allowed, "A touch with no working directory must be denied.");
        Assert.Contains(RuleId, result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowedDocumentedSamples_IsNotEmpty()
    {
        Assert.NotEmpty(AllowedDocumentedSamples());
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
