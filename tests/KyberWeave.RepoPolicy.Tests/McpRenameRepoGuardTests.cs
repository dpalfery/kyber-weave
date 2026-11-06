using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The repository-level guard that the MCP server-and-tool rename is complete on the surfaces
/// this repository ships, at both of its ends:
///
///   (a) the configuration — every checked-in MCP configuration declares the <c>kw-docs</c> server
///       key and no retired one, while the launch commands the rename deliberately does not touch
///       (<c>kyber-weave-mcp</c>, <c>kyber-weave-arbiter</c>, <c>kyberdash mcp</c>) stay exactly as
///       they were, and
///   (b) the prose and the code — no tracked file on a shipped surface still names a retired tool
///       or server in any of the forms a harness spells it.
///
/// The scan walks <c>git ls-files</c>, which is the scope that matters: a tracked file is what a
/// reader, an installer, or a rendered agent receives. Untracked build output and local tool state
/// are out of bounds by design, so a stale binary in <c>bin/</c> cannot fail the guard.
///
/// <c>docs/plans/</c> and <c>docs/archive/</c> are excluded outright: the first is the working plan
/// that has to name what it renames, and the second is the historical record. Everything else the
/// guard exempts is listed in <see cref="ExemptFiles" /> with the reason it is exempt, one exact
/// path at a time — never a prefix, so a new file next to an exempt one is scanned rather than
/// silently covered.
/// </summary>
public sealed class McpRenameRepoGuardTests
{
    private static string RepoRoot => KyberWeaveTestPaths.ToolRoot;

    // The retired tool names. Matched word-bounded, so a descriptive name that carries one as a
    // fragment is not a finding: `docs_validate_governed_document_schema` and
    // `dash_query_session_cost_tokens_and_duration` both contain a retired name and neither is one.
    private static readonly string[] RetiredToolNames =
    [
        "docs_explore",
        "docs_for_symbol",
        "docs_glossary",
        "docs_analysis_candidates",
        "docs_validate",
        "docs_drift",
        "docs_status",
        "agent_coding_standards",
        "dash_status",
        "dash_catalog",
        "dash_query",
        "dash_search_sessions",
        "dash_inspect",
        "dash_findings",
        "dash_compare",
        "dash_outcomes",
        "arbiter_evaluate",
        "arbiter_rules",
        "arbiter_status"
    ];

    // The retired server keys. The binary names that share the prefix (`kyber-weave-mcp`,
    // `kyber-weave-arbiter`, `kyberdash`) are deliberately absent: the rename moved the keys, not
    // the executables, so a guard that banned the substring would demand the wrong edit.
    private static readonly string[] RetiredServerKeys = ["kyber-weave", "kyberdash", "kyber-weave-arbiter"];

    private static readonly Regex RetiredToolNamePattern = new(
        @"\b(" + string.Join("|", RetiredToolNames) + @")\b",
        RegexOptions.CultureInvariant);

    // A tool id namespaced by the old server name: `kyber-weave/docs_explore` in a grant list,
    // `kyber-weave_*` in a permission map, `kyber-weave_agent_coding_standards` in a probe. The
    // tail is restricted to a tool-shaped identifier or a wildcard on purpose — without that, the
    // guard matches the `.kyber-weave/kyber-weave.yml` path and the repository URL
    // `github.com/dpalfery/kyber-weave/issues`, neither of which is a tool namespace.
    private static readonly Regex RetiredToolIdPattern = new(
        @"\bkyber-weave[/_](?:\*|[a-z][a-z0-9]*(?:_[a-z0-9]+)+)",
        RegexOptions.CultureInvariant);

    // The host-qualified server namespace: `mcp__kyber-weave__docs_explore` on Claude and Command
    // Code, `mcp__kyber_weave__<tool>` on Pi. The adapter's underscore form and the hyphenated form
    // both start here.
    private static readonly Regex RetiredServerNamespacePattern = new(
        @"mcp__kyber",
        RegexOptions.CultureInvariant);

    // A retired key in a JSON configuration. The trailing colon is what makes it a key rather than
    // prose that mentions the server.
    private static readonly Regex RetiredJsonServerKeyPattern = new(
        @"""(?:kyber-weave-arbiter|kyber-weave|kyberdash)""\s*:",
        RegexOptions.CultureInvariant);

    // A retired key declared in YAML on a line of its own, as `required-servers` and every other
    // server declaration writes it. A scalar value is not matched: `kyber-weave: ok` is prose about
    // the server, not a key that launches one.
    private static readonly Regex RetiredYamlServerKeyPattern = new(
        @"^[ \t]+(?:kyber-weave-arbiter|kyber-weave|kyberdash)[ \t]*:[ \t]*(?:$|[\[{])",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // `kyber-weave doctor` branches on this check id and `doctor --json` publishes it, so the id is
    // a stable contract that outlives the rename: the tool the check calls is renamed, the id it
    // reports is not. Its occurrences are masked before the tool scan rather than exempting the
    // files that carry them, because those files have to keep naming the new tools everywhere else.
    private static readonly Regex DoctorCheckIdPattern = new(
        @"mcp\.docs_explore",
        RegexOptions.CultureInvariant);

    private static readonly (string Description, Regex Pattern)[] RetiredForms =
    [
        ("retired tool name", RetiredToolNamePattern),
        ("retired tool id namespace", RetiredToolIdPattern),
        ("retired server namespace", RetiredServerNamespacePattern),
        ("retired server key", RetiredJsonServerKeyPattern),
        ("retired YAML server key", RetiredYamlServerKeyPattern)
    ];

    // The checked-in MCP configurations that declare a first-party server, in the shape each
    // harness reads. `.kyber-weave/kyber-weave.yml` holds the same declaration in YAML and is
    // checked by its own test.
    private static readonly string[] JsonMcpConfigs =
    [
        ".mcp.json",
        ".cursor/mcp.json",
        ".factory/mcp.json",
        "kilo.json",
        "products/kyber-squad/mcp.json"
    ];

    // The suffix a Windows install appends to an executable's file name: how a program is spelled on
    // that platform rather than which program it is. Accepted on every platform, because a committed
    // configuration is read on each of them.
    private const string WindowsExecutableSuffix = ".exe";

    // A renamed key still launches the binary it always launched. The rename is a naming change,
    // not a distribution change: `kw-docs` runs `kyber-weave-mcp`, `kw-arbiter` runs
    // `kyber-weave-arbiter`, and `kw-dash` runs the dashboard binary's `mcp` verb. An entry that
    // launched a renamed executable would have been fixed by renaming the wrong thing.
    private static readonly IReadOnlyDictionary<string, string> ShippedLaunchCommands =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kw-docs"] = "kyber-weave-mcp",
            ["kw-arbiter"] = "kyber-weave-arbiter",
            ["kw-dash"] = "kyberdash"
        };

    // The scope the rename has to reach: source, harness scripts, the squad product, the shipped
    // skills and documentation, plus the root markdown a reader meets first.
    private static readonly string[] InScopeRoots =
    [
        "src/",
        "dash/src/",
        "dash/scripts/",
        "products/",
        ".apm/",
        "docs/"
    ];

    // `docs/plans/` is the working plan, which has to name what it renames; `docs/archive/` is the
    // historical record. Neither is a shipped surface, so neither is scanned or exempted one file
    // at a time.
    private static readonly string[] ExcludedPrefixes = ["docs/plans/", "docs/archive/", "tests/"];

    /// <summary>
    /// Every file the scan exempts, each with the reason a reader needs to see why. The list is
    /// exact paths: an entry covers the file it names and nothing else.
    /// </summary>
    private static readonly IReadOnlyList<AllowListEntry> ExemptFiles =
    [
        new AllowListEntry(
            "src/KyberWeave.Core.Docs/Mcp/McpServerRenamer.cs",
            "Holds the retired server keys as the rename table `docs mcp-migrate` applies. A rename " +
            "table that could not name the keys it renames could not rename them."),
        new AllowListEntry(
            "docs/install.md",
            "Carries the old-to-new rename table an operator works from when migrating an existing " +
            "install, which has to show the retired name beside its replacement."),
        new AllowListEntry(
            "docs/docgraph/mcp-runbook.md",
            "Carries the old-to-new rename table, and the probe recipes that call the retired tool " +
            "by name to show an old install failing the new check."),
        new AllowListEntry(
            "docs/dash/onboarding.md",
            "Carries the old-to-new rename table for the dashboard tools' onboarding path."),
        new AllowListEntry(
            "docs/adr/0058-mcp-servers-and-tools-are-named-by-purpose.md",
            "The decision record that decided the rename. Its subject is the retired naming, so it " +
            "states the retired names.")
    ];

    /// <summary>
    /// The rename is complete in the checked-in JSON configurations: each declares the
    /// <c>kw-docs</c> server and no retired key.
    /// </summary>
    [Fact]
    public void CheckedInConfigsDeclareTheKwDocsKeyAndNoRetiredServerKey()
    {
        List<string> violations = [];

        foreach (string relativePath in JsonMcpConfigs)
        {
            IReadOnlyList<ServerEntry> entries = ReadServerEntries(relativePath);

            if (!entries.Any(entry => string.Equals(entry.Name, "kw-docs", StringComparison.Ordinal)))
            {
                violations.Add(
                    $"{relativePath}: declares no kw-docs server (it declares " +
                    $"{string.Join(", ", entries.Select(static entry => entry.Name))})");
            }

            foreach (ServerEntry entry in entries)
            {
                if (RetiredServerKeys.Contains(entry.Name, StringComparer.Ordinal))
                {
                    violations.Add(
                        $"{relativePath}: declares the retired server key \"{entry.Name}\" (launch " +
                        $"{string.Join(' ', entry.Command)}); rename it to kw-docs, kw-arbiter or kw-dash");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"{violations.Count} checked-in MCP configuration problem(s): the rename renames the server " +
            "keys and leaves the launch commands alone. Edit the configuration:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>
    /// The rename moved the keys, not the executables: every first-party entry still launches the
    /// binary it launched before, with the subcommand it always used.
    /// </summary>
    [Fact]
    public void CheckedInConfigsKeepTheShippedLaunchCommands()
    {
        List<string> violations = [];

        foreach (string relativePath in JsonMcpConfigs)
        {
            foreach (ServerEntry entry in ReadServerEntries(relativePath))
            {
                if (!entry.Name.StartsWith("kw-", StringComparison.Ordinal))
                {
                    continue;
                }

                violations.AddRange(LaunchViolations(relativePath, entry.Name, entry.Command));
            }
        }

        Assert.True(
            violations.Count == 0,
            $"{violations.Count} first-party MCP entry problem(s): the rename renames the server key " +
            "and must leave the launch command alone. Restore the shipped launch:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>
    /// The launch contract on its own, so the <c>kw-dash</c> row is decided by a test rather than by
    /// whether a checked-in configuration happens to declare the dashboard server today.
    /// </summary>
    [Fact]
    public void AFirstPartyEntryLaunchesItsShippedBinaryAndSubcommand()
    {
        Assert.Empty(LaunchViolations("fixture.json", "kw-docs", ["kyber-weave-mcp", "--repo-root", "."]));
        Assert.Empty(LaunchViolations("fixture.json", "kw-arbiter", ["kyber-weave-arbiter", "serve"]));
        Assert.Empty(LaunchViolations("fixture.json", "kw-dash", ["kyberdash", "mcp"]));

        // The launch the runbook documents for a client started from a desktop icon: the same
        // executable, spelled with the absolute directory it lives in. The rename moved the key and
        // left the executable alone, so this is the shipped launch and must not be a finding.
        Assert.Empty(LaunchViolations(
            "fixture.json",
            "kw-docs",
            ["${userHome}/.local/bin/kyber-weave-mcp", "--repo-root", "."]));

        // Accepting a directory is not accepting any name: the launch's file name still has to be
        // the shipped binary's, so a renamed key that launches another program is still a violation,
        // and so is a path-qualified lookalike that merely carries the shipped name.
        Assert.NotEmpty(LaunchViolations("fixture.json", "kw-docs", ["kyberdash"]));
        Assert.NotEmpty(
            LaunchViolations("fixture.json", "kw-docs", ["/usr/local/bin/fake-kyber-weave-mcp", "--repo-root", "."]));

        // The dashboard's `mcp` verb is still part of the launch `kw-dash` has to keep.
        Assert.NotEmpty(LaunchViolations("fixture.json", "kw-dash", ["kyberdash"]));

        // A key renamed without its launch, a launch renamed to the key, and the dashboard's `mcp`
        // verb dropped are the three ways this goes wrong.
        Assert.NotEmpty(LaunchViolations("fixture.json", "kw-docs", []));
        Assert.NotEmpty(LaunchViolations("fixture.json", "kw-docs", ["kw-docs"]));
        Assert.NotEmpty(LaunchViolations("fixture.json", "kw-dash", ["kyberdash"]));
        Assert.NotEmpty(LaunchViolations("fixture.json", "kw-dash", ["kyberdash", "mcp-server"]));
        Assert.NotEmpty(LaunchViolations("fixture.json", "kw-docs", ["kyberdash", "mcp"]));
    }

    /// <summary>
    /// The repository's own manifest holds the same declaration in YAML, under
    /// <c>required-servers</c>, and <c>docs init</c> never rewrites it once it exists — so a stale
    /// key here stays stale until someone edits it.
    /// </summary>
    [Fact]
    public void CheckedInManifestDeclaresTheKwDocsKeyAndNoRetiredServerKey()
    {
        string content = File.ReadAllText(Path.Combine(RepoRoot, ".kyber-weave", "kyber-weave.yml"));

        Assert.True(
            content.Contains("required-servers:", StringComparison.Ordinal),
            ".kyber-weave/kyber-weave.yml no longer declares mcp.required-servers, so this guard is " +
            "checking the wrong section.");

        Assert.True(
            Regex.IsMatch(content, @"^[ \t]+kw-docs[ \t]*:[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Multiline),
            ".kyber-weave/kyber-weave.yml: required-servers declares no kw-docs server.");

        MatchCollection retired = RetiredYamlServerKeyPattern.Matches(content);
        Assert.True(
            retired.Count == 0,
            ".kyber-weave/kyber-weave.yml: required-servers still declares the retired key(s) " +
            $"{string.Join(", ", retired.Select(static match => match.Value.Trim()))}. The launch " +
            "command stays `kyber-weave-mcp`; only the key is renamed.");

        Assert.Contains("kyber-weave-mcp", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// `.vscode/mcp.json` is unchanged in intent: it carries no first-party entry, so the rename has
    /// nothing to do there and a <c>kw-</c> key appearing in it would mean an entry was added.
    /// </summary>
    [Fact]
    public void CheckedInEditorConfigDeclaresNoFirstPartyEntry()
    {
        string content = File.ReadAllText(Path.Combine(RepoRoot, ".vscode", "mcp.json"));

        using JsonDocument document = JsonDocument.Parse(
            content,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        Assert.True(
            document.RootElement.TryGetProperty("servers", out JsonElement servers),
            ".vscode/mcp.json no longer declares a `servers` object, so this guard is checking the " +
            "wrong section.");

        List<string> firstParty =
        [
            .. servers.EnumerateObject()
                .Select(static server => server.Name)
                .Where(static name => name.StartsWith("kw-", StringComparison.Ordinal)
                                      || name.Contains("kyber", StringComparison.OrdinalIgnoreCase))
        ];

        Assert.True(
            firstParty.Count == 0,
            ".vscode/mcp.json is unchanged in intent: it holds no first-party entry, so the rename " +
            $"has nothing to do there. It now declares: {string.Join(", ", firstParty)}");
    }

    /// <summary>
    /// No tracked file on a shipped surface still names a retired tool or server in any form a
    /// harness spells it.
    /// </summary>
    [Fact]
    public void NoShippedSurfaceNamesARetiredToolOrServerKey()
    {
        List<string> trackedFiles = ListTrackedFiles();
        ConcurrentBag<string> offenders = new();
        int scanned = 0;

        Parallel.ForEach(trackedFiles, relativePath =>
        {
            if (!IsInScope(relativePath) || IsExempt(relativePath))
            {
                return;
            }

            string content;
            try
            {
                content = ReadAsLatin1(relativePath);
            }
            catch (IOException)
            {
                return; // deleted between listing and reading; not tracked content
            }

            Interlocked.Increment(ref scanned);

            IReadOnlyList<string> forms = FindRetiredForms(content);
            if (forms.Count > 0)
            {
                offenders.Add($"{relativePath} ({forms.Count}): {string.Join("; ", forms.Take(4))}");
            }
        });

        List<string> sorted = [.. offenders.OrderBy(static path => path, StringComparer.Ordinal)];
        Assert.True(
            sorted.Count == 0,
            $"{sorted.Count} tracked file(s) on a shipped surface still name a retired MCP tool or " +
            "server. Use the `kw-` server key and the descriptive tool name, move the record into " +
            "docs/archive/ if it is history, or add the file to ExemptFiles in this guard with the " +
            $"reason it is exempt. ({scanned} file(s) scanned.)\n  " + string.Join("\n  ", sorted));
    }

    /// <summary>
    /// The scan has to be able to find what it bans, or it would pass as quietly on a rename that
    /// never happened as on one that is complete.
    /// </summary>
    [Fact]
    public void TheLegacyScanFindsTheFormsItBans()
    {
        foreach (string tool in RetiredToolNames)
        {
            Assert.NotEmpty(FindRetiredForms($"The server answers `{tool}` with a section."));
        }

        Assert.NotEmpty(FindRetiredForms("tools: [\"mcp__kyber-weave__docs_answer_question\"]"));
        Assert.NotEmpty(FindRetiredForms("tools: [\"mcp__kyber_weave__agent_code_standards\"]"));
        Assert.NotEmpty(FindRetiredForms("permission: {kyber-weave_*: allow}"));
        Assert.NotEmpty(FindRetiredForms("grants: [kyber-weave/*, codegraph/*]"));
        Assert.NotEmpty(FindRetiredForms("grants: [kyber-weave/docs_explore]"));
        Assert.NotEmpty(FindRetiredForms("{\n  \"mcpServers\": {\n    \"kyber-weave\": {\n"));
        Assert.NotEmpty(FindRetiredForms("{\n  \"mcpServers\": {\n    \"kyberdash\": {\n"));
        Assert.NotEmpty(FindRetiredForms("{\n  \"mcpServers\": {\n    \"kyber-weave-arbiter\": {\n"));
        Assert.NotEmpty(FindRetiredForms("mcp:\n  required-servers:\n    kyber-weave:\n"));

        // The names that carry a retired name as a fragment are not findings.
        Assert.Empty(FindRetiredForms("call docs_validate_governed_document_schema instead"));
        Assert.Empty(FindRetiredForms("metric dash_query_session_cost_tokens_and_duration"));

        // The binaries and the paths that share the prefix are not findings either: the rename
        // moves keys and tool names, never the executables or the product's own directory.
        Assert.Empty(FindRetiredForms("command: [kyber-weave-mcp, --repo-root, .]"));
        Assert.Empty(FindRetiredForms("command: kyber-weave-arbiter serve --repo-root ."));
        Assert.Empty(FindRetiredForms("kyber-weave docs validate ."));
        Assert.Empty(FindRetiredForms("edit .kyber-weave/kyber-weave.yml, then `.kyber-weave/squad.lock.yml`"));
        Assert.Empty(FindRetiredForms("https://github.com/dpalfery/kyber-weave/issues/386"));

        // The doctor check id is a stable contract and outlives the rename.
        Assert.Empty(FindRetiredForms("| `mcp.docs_explore` | the call returned an error |"));
        Assert.NotEmpty(FindRetiredForms("a `tools/call` to `docs_explore` returns a section"));
    }

    /// <summary>
    /// The scope is the shipped surfaces only: a plan or an archived record may name what the rename
    /// retired, and a test may name it because a guard has to name what it forbids.
    /// </summary>
    [Theory]
    [InlineData("AGENTS.md", true)]
    [InlineData("README.md", true)]
    [InlineData("src/KyberWeave.Mcp/DocsTools.cs", true)]
    [InlineData("dash/src/analysis/query/aggregate.ts", true)]
    [InlineData("dash/scripts/build-sbom.mjs", true)]
    [InlineData("products/kyber-squad/mcp.json", true)]
    [InlineData(".apm/skills/codegraph/SKILL.md", true)]
    [InlineData("docs/install.md", true)]
    [InlineData("docs/plans/mcp-server-and-tool-rename.md", false)]
    [InlineData("docs/archive/plans/2026-10-31-universal-kyber-weave-mcp-grant.md", false)]
    [InlineData("tests/KyberWeave.RepoPolicy.Tests/UpstreamSeveranceTests.cs", false)]
    [InlineData("tests/KyberWeave.Tests/McpLiveProbeTests.cs", false)]
    [InlineData("dash/src/analysis/query/aggregate.test.ts", false)]
    [InlineData("scripts/proc-guard.sh", false)]
    public void TheLegacyScanCoversTheSurfacesItClaims(string repoRelativePath, bool inScope)
    {
        Assert.Equal(inScope, IsInScope(repoRelativePath));
    }

    /// <summary>
    /// An exemption is a reason attached to one exact file. A glob or a directory prefix would exempt
    /// files nobody reviewed, and a reason-less entry would leave a reader unable to tell why.
    /// </summary>
    [Fact]
    public void EveryExemptFileStatesItsReasonAndTheExemptionIsExact()
    {
        Assert.NotEmpty(ExemptFiles);
        Assert.Equal(
            ExemptFiles.Count,
            ExemptFiles.Select(static entry => entry.Path).Distinct(StringComparer.Ordinal).Count());

        foreach (AllowListEntry entry in ExemptFiles)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Reason),
                $"{entry.Path} is exempt from the rename scan with no reason stated.");
            Assert.DoesNotContain('*', entry.Path);
            Assert.False(
                entry.Path.EndsWith('/'),
                $"{entry.Path} is a directory prefix; an exemption names one exact file.");
            Assert.True(
                IsInScope(entry.Path),
                $"{entry.Path} is exempt but outside the scan's scope, so the entry exempts nothing.");
            Assert.True(IsExempt(entry.Path), $"{entry.Path} is listed as exempt but IsExempt does not exempt it.");
        }

        // Exact file, never a prefix: the directory, its siblings, and a longer path that starts the
        // same way are all still scanned.
        Assert.False(IsExempt("docs"));
        Assert.False(IsExempt("docs/dash"));
        Assert.False(IsExempt("docs/dash/README.md"));
        Assert.False(IsExempt("src/KyberWeave.Core.Docs/Mcp/"));
        Assert.False(IsExempt("src/KyberWeave.Core.Docs/Mcp/McpWiringInspector.cs"));
        Assert.False(IsExempt("docs/install.md.bak"));
    }

    /// <summary>
    /// The scan reads the tracked tree, not an empty list: a listing that failed silently would make
    /// the offender assertion above vacuous.
    /// </summary>
    [Fact]
    public void TheScanReadsTheTrackedTree()
    {
        List<string> trackedFiles = ListTrackedFiles();

        Assert.True(
            trackedFiles.Count > 200,
            $"`git ls-files` returned {trackedFiles.Count} path(s), which is too few for this " +
            "repository: the listing failed rather than finding a small tree.");
        Assert.Contains("AGENTS.md", trackedFiles);

        int inScope = trackedFiles.Count(IsInScope);
        Assert.True(
            inScope > 100,
            $"{inScope} tracked path(s) are inside the scan's scope, which is too few for this " +
            "repository: the scope rules stopped matching the tree.");
    }

    private static IEnumerable<string> LaunchViolations(
        string relativePath,
        string serverKey,
        IReadOnlyList<string> command)
    {
        if (command.Count == 0)
        {
            yield return $"{relativePath}: \"{serverKey}\" declares no command";
            yield break;
        }

        if (!ShippedLaunchCommands.TryGetValue(serverKey, out string? expected))
        {
            yield return
                $"{relativePath}: \"{serverKey}\" is a first-party key with no shipped launch " +
                "recorded here; add its binary to ShippedLaunchCommands and to the docs";
            yield break;
        }

        // The proof that a launch runs the shipped binary is the binary's FILE NAME, not the exact
        // string a configuration happens to spell. That is the plan's proof rule, and it is the one
        // the code already applies on both sides of a rename: `McpServerRenamer.IsProven` proves a
        // candidate entry by its file name (any directory, either case, an optional `.exe`) and
        // `RequiredMcpServer.NamesShippedBinary` accepts a first-party claim the same way. The
        // runbook is why it matters here: it tells an operator whose client is launched from a
        // desktop icon to use an ABSOLUTE path, because a GUI process inherits a minimal PATH that
        // usually excludes `~/.local/bin` — so `.cursor/mcp.json` launches
        // `${userHome}/.local/bin/kyber-weave-mcp`. Comparing the whole argv[0] ordinally rejected
        // that documented launch for a rename that only ever moved the key.
        if (!LaunchesShippedBinary(command[0], expected))
        {
            yield return
                $"{relativePath}: \"{serverKey}\" launches \"{command[0]}\" where it launched " +
                $"\"{expected}\"; the rename renames the key, not the executable";
            yield break;
        }

        // `kw-dash` reaches the dashboard through its `mcp` verb rather than a dedicated binary, so
        // the subcommand is part of the launch it must keep.
        if (string.Equals(serverKey, "kw-dash", StringComparison.Ordinal)
            && (command.Count < 2 || !string.Equals(command[1], "mcp", StringComparison.Ordinal)))
        {
            yield return
                $"{relativePath}: \"{serverKey}\" must launch `kyberdash mcp`; the launch is " +
                $"`{string.Join(' ', command)}`";
        }
    }

    /// <summary>
    /// Whether <paramref name="program" /> is the shipped binary <paramref name="expected" />, by the
    /// repository's proof rule: argv[0]'s FILE NAME is the binary's, any directory, either case, an
    /// optional <c>.exe</c> — the rule <c>McpServerRenamer.IsProven</c> applies to a rename candidate
    /// and <c>RequiredMcpServer.NamesShippedBinary</c> applies to a first-party claim.
    /// </summary>
    /// <remarks>
    /// The comparison is the whole file name, never a substring: a directory prefix and the case a
    /// file system ignores are spelling, while <c>fake-kyber-weave-mcp</c> is a different program.
    /// </remarks>
    private static bool LaunchesShippedBinary(string program, string expected)
    {
        string fileName = Path.GetFileName(program);

        return string.Equals(fileName, expected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, expected + WindowsExecutableSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ServerEntry> ReadServerEntries(string relativePath)
    {
        string content = File.ReadAllText(Path.Combine(RepoRoot, relativePath));
        using JsonDocument document = JsonDocument.Parse(
            content,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("mcpServers", out JsonElement servers)
            && !root.TryGetProperty("mcp", out servers))
        {
            throw new InvalidOperationException(
                $"{relativePath} declares neither an `mcpServers` nor an `mcp` object, so this " +
                "guard is checking the wrong section.");
        }

        List<ServerEntry> entries = [];
        foreach (JsonProperty server in servers.EnumerateObject())
        {
            List<string> command = [];
            if (server.Value.TryGetProperty("command", out JsonElement value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    command.Add(value.GetString() ?? string.Empty);
                }
                else if (value.ValueKind == JsonValueKind.Array)
                {
                    command.AddRange(value.EnumerateArray().Select(static token => token.GetString() ?? string.Empty));
                }
            }

            entries.Add(new ServerEntry(server.Name, command));
        }

        return entries;
    }

    /// <summary>
    /// Every retired form in <paramref name="content" />, as a description a failure message can
    /// print. The doctor check id is masked first: it stays the same name after the rename.
    /// </summary>
    private static IReadOnlyList<string> FindRetiredForms(string content)
    {
        string scanned = DoctorCheckIdPattern.Replace(content, "doctor-check-id");
        List<string> found = [];

        foreach ((string description, Regex pattern) in RetiredForms)
        {
            foreach (Match match in pattern.Matches(scanned))
            {
                found.Add($"{description} \"{match.Value.Trim()}\" at line {LineNumber(scanned, match.Index)}");
            }
        }

        return found;
    }

    private static int LineNumber(string content, int index)
    {
        int line = 1;
        for (int position = 0; position < index; position++)
        {
            if (content[position] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string ReadAsLatin1(string repoRelativePath)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(RepoRoot, repoRelativePath));

        // Latin-1 maps every byte to a char, so the ASCII forms are found in text and in the
        // fixtures beside it (JSONL event logs, SBOM fixtures) without throwing on invalid UTF-8.
        return Encoding.Latin1.GetString(bytes);
    }

    private static bool IsInScope(string repoRelativePath)
    {
        string normalized = repoRelativePath.Replace('\\', '/');

        if (ExcludedPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return false;
        }

        if (normalized.EndsWith(".test.ts", StringComparison.Ordinal))
        {
            return false;
        }

        // The root markdown is what a reader meets first: README, AGENTS, CONTRIBUTING.
        if (!normalized.Contains('/', StringComparison.Ordinal) && normalized.EndsWith(".md", StringComparison.Ordinal))
        {
            return true;
        }

        return InScopeRoots.Any(root => normalized.StartsWith(root, StringComparison.Ordinal));
    }

    private static bool IsExempt(string repoRelativePath)
    {
        string normalized = repoRelativePath.Replace('\\', '/');
        return ExemptFiles.Any(entry => string.Equals(entry.Path, normalized, StringComparison.Ordinal));
    }

    private static List<string> ListTrackedFiles()
    {
        List<string> tracked = [];

        // `git ls-files -z` through StdioSession, the repository's second bounded spawn path, rather
        // than a fresh ProcessRunner site in this file: the process-spawn audit keys its allow-list
        // by file and line, and the identical listing already runs in UpstreamSeveranceTests, so a
        // second copy of the same site would be a second entry for the same bound. The session kills
        // the child tree on dispose whatever the outcome, and its 60 s budget bounds the wait. The
        // listing is one sizeable line, far below the session's 1 MiB line cap; a repository large
        // enough to cross it fails this guard loudly instead of scanning a shortened list. The
        // session starts the child in the test's own working directory, so `-C` sets the root
        // rather than a shell `cd`.
        using StdioSession session = new("git", ["-C", RepoRoot, "ls-files", "-z"], TimeSpan.FromSeconds(60));

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

            // -z separates entries with NUL, so paths with spaces or quotes stay one entry each.
            tracked.AddRange(line.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }

        return tracked;
    }

    /// <summary>One first-party server entry, as the configuration declares it.</summary>
    private sealed record ServerEntry(string Name, IReadOnlyList<string> Command);

    /// <summary>An exempt file and the reason a reader needs to see why it is exempt.</summary>
    private sealed record AllowListEntry(string Path, string Reason);
}
