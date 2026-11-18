using KyberWeave.Tests.Configuration;

using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;
using YamlDotNet.Core;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>
/// This repository's own <c>review</c> gate configuration, read from the checked-in <c>.kyber-weave/kyber-weave.yml</c>:
/// which task gates each developer agent runs, the include globs, and the flags the dotnet gates carry. How a gate is
/// parsed is pinned over in-memory yaml by <c>ReviewTaskGatesConfigTests</c>.
/// </summary>
public sealed class ReviewTaskGatesRepositoryConfigTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>
    ///     The checked-in configuration is what this repository's own developers run. Declaring the
    ///     gates there is the point of the sections, so both sets are pinned: a missing technology or
    ///     role is a silent gap, and an extra one is a gate nobody reviewed for it.
    /// </summary>
    [Fact]
    public void RepositoryConfigDeclaresDeveloperTaskGates()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = RepositoryTaskGates();
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> technologyGates = RepositoryTechnologyGates();

        // Every technology the removed per-technology agents used to be the key for is now a
        // technology key, so its worker resolves the same gates through the task's technology.
        string[] technologies =
        [
            "csharp",
            "github-actions",
            "react",
            "tauri",
            "typescript",
            "rust"
        ];

        string[] roles =
        [
            // These roles do not map to one technology, so they keep an agent-keyed entry that the
            // resolver reads first.
            "sql-database-architect",
            "test-dev",

            // docs-dev joined this set when the conductor run-speed programme gave it its first
            // gate (that programme's D60): a config-only edit in this repository is a docs-dev
            // edit, so the one role that most often touches the file deciding every other gate was
            // the one role with nothing checking it. Its single gate is `review-config`.
            "docs-dev"
        ];

        Assert.Equal(technologies.Order(StringComparer.Ordinal), technologyGates.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(roles.Order(StringComparer.Ordinal), taskGates.Keys.Order(StringComparer.Ordinal));
        Assert.All(technologies, technology => Assert.NotEmpty(technologyGates[technology]));
        Assert.All(roles, role => Assert.NotEmpty(taskGates[role]));
        Assert.Equal(["dash-typecheck", "dash-lint"], technologyGates["typescript"].Select(gate => gate.Id));
        Assert.Equal(["cargo-fmt", "cargo-clippy"], technologyGates["rust"].Select(gate => gate.Id));
        Assert.Equal(["review-config"], taskGates["docs-dev"].Select(gate => gate.Id));
    }

    /// <summary>
    ///     Python developers and the non-developer roles do not run a fix-and-verify gate against their
    ///     own change, so declaring one for them would make the task gate run where it was never
    ///     reviewed.
    /// </summary>
    [Fact]
    public void RepositoryConfigDeclaresNoTaskGatesForPythonOrNonDeveloperAgents()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = RepositoryTaskGates();

        string[] notDevelopers =
        [
            "python-dev",
            "architect",
            "conductor",
            "research-agent",
            "code-reviewer"
        ];

        Assert.All(notDevelopers, agent => Assert.DoesNotContain(agent, taskGates.Keys));
    }

    /// <summary>
    ///     The Go developer runs no task gate of its own; python-dev is pinned in the test above. A Go
    ///     gate added without its own review would run the dash or tray suites on a change that never
    ///     touched them.
    /// </summary>
    [Fact]
    public void GoDeveloperDeclaresNoTaskGates()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = RepositoryTaskGates();

        string[] agents =
        [
            "go-dev"
        ];

        Assert.All(agents, agent => Assert.DoesNotContain(agent, taskGates.Keys));
    }

    /// <summary>
    ///     The <c>typescript</c> and <c>rust</c> technology entries reuse the same gates the
    ///     <c>react</c> and <c>tauri</c> entries run. A copy that drifted (a different include list
    ///     or command) would let the neighbouring technologies check different things against the
    ///     same dash and tray code.
    /// </summary>
    [Fact]
    public void TypeScriptAndRustDevelopersReuseTheSharedDashAndTrayGates()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> technologyGates = RepositoryTechnologyGates();

        IReadOnlyList<ReviewTaskGate> react = technologyGates["react"];
        IReadOnlyList<ReviewTaskGate> typescript = technologyGates["typescript"];
        string[] dashIds =
        [
            "dash-typecheck",
            "dash-lint"
        ];
        foreach (string id in dashIds)
        {
            ReviewTaskGate expected = Assert.Single(react, gate => gate.Id == id);
            ReviewTaskGate actual = Assert.Single(typescript, gate => gate.Id == id);

            Assert.Equal(expected.Include, actual.Include);
            Assert.Equal(expected.Run, actual.Run);
            Assert.Equal(expected.Fix, actual.Fix);
        }

        IReadOnlyList<ReviewTaskGate> tauri = technologyGates["tauri"];
        IReadOnlyList<ReviewTaskGate> rust = technologyGates["rust"];
        string[] cargoIds =
        [
            "cargo-fmt",
            "cargo-clippy"
        ];
        foreach (string id in cargoIds)
        {
            ReviewTaskGate expected = Assert.Single(tauri, gate => gate.Id == id);
            ReviewTaskGate actual = Assert.Single(rust, gate => gate.Id == id);

            Assert.Equal(expected.Include, actual.Include);
            Assert.Equal(expected.Run, actual.Run);
            Assert.Equal(expected.Fix, actual.Fix);
        }
    }

    /// <summary>
    ///     The test developer changes the tray's Rust tests as well as the .NET and dash ones, so it
    ///     runs the tray's format and lint gates alongside its existing set. Without them a Rust
    ///     formatting or clippy failure in a test-only change reaches the reviewer unchecked.
    /// </summary>
    [Fact]
    public void TestDevRunsTheTrayRustFormatAndLintGates()
    {
        IReadOnlyList<ReviewTaskGate> gates = RepositoryTaskGates()["test-dev"];

        // test-dev keeps an agent-keyed entry because its work spans technologies, and aliases the
        // gates the rust technology declares.
        IReadOnlyList<ReviewTaskGate> rust = RepositoryTechnologyGates()["rust"];
        foreach (string id in new[] { "cargo-fmt", "cargo-clippy" })
        {
            ReviewTaskGate expected = Assert.Single(rust, gate => gate.Id == id);
            ReviewTaskGate actual = Assert.Single(gates, gate => gate.Id == id);

            Assert.Equal(expected.Include, actual.Include);
            Assert.Equal(expected.Run, actual.Run);
            Assert.Equal(expected.Fix, actual.Fix);
        }

        Assert.Contains(gates, gate => gate.Id == "dash-typecheck");
        Assert.Contains(gates, gate => gate.Id == "dotnet-build");
    }

    /// <summary>
    ///     The review council runs the tray's Rust suite the same way CI does: format check, clippy
    ///     with warnings denied, then tests, all after the TypeScript gates and all blocking. Clippy
    ///     and tests carry a 1800-second limit, so a slow Cargo build is not cut off at the default.
    /// </summary>
    [Fact]
    public void ReviewGatesRunTheTrayRustSuiteThatCiRuns()
    {
        IReadOnlyList<ReviewGate> gates = RepositoryReviewGates();
        List<string> ids = gates.Select(gate => gate.Id).ToList();

        int tsReachable = ids.IndexOf("ts-reachable");
        Assert.True(tsReachable >= 0, "Expected the review gates to end with ts-reachable.");
        Assert.Equal(["cargo-fmt", "cargo-clippy", "cargo-test"], ids.Skip(tsReachable + 1).Take(3));

        ReviewGate fmt = gates[tsReachable + 1];
        ReviewGate clippy = gates[tsReachable + 2];
        ReviewGate test = gates[tsReachable + 3];

        Assert.True(fmt.Blocking, "cargo-fmt must block.");
        Assert.Equal(900, fmt.TimeoutSeconds);
        Assert.Equal(
            ["cargo", "fmt", "--manifest-path", "dash/tray/src-tauri/Cargo.toml", "--check"],
            fmt.Run);

        Assert.True(clippy.Blocking, "cargo-clippy must block.");
        Assert.Equal(1800, clippy.TimeoutSeconds);
        Assert.Equal(
            ["cargo", "clippy", "--manifest-path", "dash/tray/src-tauri/Cargo.toml", "--", "-D", "warnings"],
            clippy.Run);

        Assert.True(test.Blocking, "cargo-test must block.");
        Assert.Equal(1800, test.TimeoutSeconds);
        Assert.Equal(
            ["cargo", "test", "--manifest-path", "dash/tray/src-tauri/Cargo.toml"],
            test.Run);

        // The council gate and the per-task gate run the same argv, so a task that passes
        // its gate passes the council's. The rust technology entry is the per-task one.
        IReadOnlyList<ReviewTaskGate> rustTaskGates = RepositoryTechnologyGates()["rust"];
        Assert.Equal(Assert.Single(rustTaskGates, gate => gate.Id == "cargo-fmt").Run, fmt.Run);
        Assert.Equal(Assert.Single(rustTaskGates, gate => gate.Id == "cargo-clippy").Run, clippy.Run);
    }

    /// <summary>
    ///     A change to the solution file, a NuGet.config, or an .editorconfig changes what the build
    ///     compiles and how the analyzers read it, so the build gate must run for it. The format gates
    ///     must not match those files: <c>dotnet format</c> given a solution in <c>--include</c> checks
    ///     nothing and exits 0, so a format gate that matched the solution would pass without checking.
    /// </summary>
    [Fact]
    public void SolutionAndBuildConfigChangesRunTheBuildGateButNotTheFormatGates()
    {
        IReadOnlyList<ReviewTaskGate> gates = RepositoryTechnologyGates()["csharp"];
        string[] buildInputs =
        [
            "KyberWeave.sln",
            "KyberWeave.slnx",
            "NuGet.config",
            "src/KyberWeave.Core/NuGet.config",
            ".editorconfig"
        ];

        ReviewTaskGate build = Assert.Single(gates, gate => gate.Id == "dotnet-build");
        foreach (string path in buildInputs)
            Assert.True(
                build.Include.Any(pattern => PathGlob.IsMatch(pattern, path)),
                $"dotnet-build should run for a change to '{path}'.");

        foreach (ReviewTaskGate format in gates.Where(gate => gate.Id.StartsWith("format-", StringComparison.Ordinal)))
            Assert.DoesNotContain(
                buildInputs,
                path => format.Include.Any(pattern => PathGlob.IsMatch(pattern, path)));
    }

    /// <summary>
    ///     T5 RED (issue #386 item E): concurrent workers share nothing when no MSBuild node is
    ///     reused and no Roslyn server is shared across them. Every <c>dotnet-build</c> task gate
    ///     therefore carries <c>-nr:false -p:UseSharedCompilation=false</c>. Only the build gates
    ///     are pinned: the format gates do not compile, so node reuse and shared compilation do not
    ///     apply to them. The repository gates below name neither flag yet, so this fails until T5
    ///     GREEN adds both flags to the task-gate build argv.
    /// </summary>
    [Fact]
    public void DotnetBuildTaskGatesCarryNoNodeReuseAndNoSharedCompilation()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = RepositoryTaskGates();

        List<string> offenders = [];
        int total = 0;
        foreach (KeyValuePair<string, IReadOnlyList<ReviewTaskGate>> entry in taskGates)
            foreach (ReviewTaskGate gate in entry.Value)
            {
                if (gate.Id != "dotnet-build")
                    continue;

                total++;
                if (!HasFlag(gate.Run, "nr:false") || !HasFlag(gate.Run, "p:UseSharedCompilation=false"))
                    offenders.Add($"{entry.Key}/{gate.Id}: [{string.Join(", ", gate.Run)}]");
            }

        Assert.True(total > 0, "Expected at least one dotnet-build task gate in the repository config.");
        Assert.True(offenders.Count == 0,
            "T5 RED (issue #386 item E): these dotnet-build task gates lack -nr:false or -p:UseSharedCompilation=false:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    ///     The review <c>build</c> gate compiles the same solution the task gates compile, so it
    ///     carries the same two flags. The checked-in gate below names neither yet.
    /// </summary>
    [Fact]
    public void ReviewBuildGateCarriesNoNodeReuseAndNoSharedCompilation()
    {
        KyberWeaveConfigLoadResult result = ConfigFileReader.TryLoad(Corpus.Root);

        Assert.True(result.Success, result.Error);
        ReviewGate build = Assert.Single(result.Config!.Review.Gates, gate => gate.Id == "build");

        Assert.True(HasFlag(build.Run, "nr:false"), "Review build gate lacks -nr:false (or /nr:false).");
        Assert.True(HasFlag(build.Run, "p:UseSharedCompilation=false"),
            "Review build gate lacks -p:UseSharedCompilation=false.");
    }

    /// <summary>
    ///     T5 RED (issue #386 item E): every task gate that runs <c>dotnet</c> isolates its build
    ///     output under a per-task path, so two agents building at once never share one
    ///     <c>obj/</c> tree. A gate qualifies with <c>--artifacts-path</c> naming <c>{task}</c>, or
    ///     with <c>-p:BaseOutputPath=</c> / <c>-p:BaseIntermediateOutputPath=</c> naming
    ///     <c>{task}</c> where the command takes no <c>--artifacts-path</c>. The format gates below
    ///     name neither yet, so this fails until T5 GREEN isolates them.
    /// </summary>
    [Fact]
    public void EveryDotnetTaskGateUsesAPerTaskArtifactsPath()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = RepositoryTaskGates();

        List<string> offenders = [];
        int total = 0;
        foreach (KeyValuePair<string, IReadOnlyList<ReviewTaskGate>> entry in taskGates)
            foreach (ReviewTaskGate gate in entry.Value)
            {
                if (gate.Run.Count == 0 || !IsDotnetCommand(gate.Run[0]))
                    continue;

                total++;
                if (!HasPerTaskIsolation(gate))
                    offenders.Add($"{entry.Key}/{gate.Id}: [{string.Join(", ", gate.Run)}]");
            }

        Assert.True(total > 0, "Expected at least one dotnet task gate in the repository config.");
        Assert.True(
            offenders.Count == 0,
            "T5 RED (issue #386 item E): these dotnet task gates share one obj/ tree — " +
            "give each a per-task --artifacts-path or -p:Base*Path naming {task}:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    ///     The build gate already isolates: it writes under the task scratchpad with the
    ///     <c>{task}</c> token the runner expands per task. This pins that contract so the flag
    ///     work above cannot regress it.
    /// </summary>
    [Fact]
    public void DotnetBuildTaskGatesIsolateUnderTheTaskScratchpad()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = RepositoryTaskGates();

        List<string> offenders = [];
        foreach (KeyValuePair<string, IReadOnlyList<ReviewTaskGate>> entry in taskGates)
            foreach (ReviewTaskGate gate in entry.Value)
            {
                if (gate.Id != "dotnet-build")
                    continue;

                int artifacts = -1;
                for (int i = 0; i < gate.Run.Count; i++)
                    if (gate.Run[i] == "--artifacts-path")
                    {
                        artifacts = i;
                        break;
                    }

                bool isolated = artifacts >= 0 &&
                                artifacts + 1 < gate.Run.Count &&
                                gate.Run[artifacts + 1].Contains("{task}", StringComparison.Ordinal) &&
                                gate.Run[artifacts + 1].Contains(".agent_scratchpad/", StringComparison.Ordinal);

                if (!isolated)
                    offenders.Add($"{entry.Key}/{gate.Id}: [{string.Join(", ", gate.Run)}]");
            }

        Assert.True(offenders.Count == 0,
            "dotnet-build task gates must keep --artifacts-path under .agent_scratchpad/{task}:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    ///     Every council gate in the repository configuration must declare non-empty include globs,
    ///     ensuring each gate is scoped to the changes that actually affect it.
    /// </summary>
    [Fact]
    public void RepositoryReviewGatesDeclareIncludeGlobs()
    {
        IReadOnlyList<ReviewGate> gates = RepositoryReviewGates();

        Assert.Equal(52, gates.Count);
        List<string> missing = [];
        foreach (ReviewGate gate in gates)
            if (gate.Include.Count == 0)
                missing.Add(gate.Id);

        Assert.True(
            missing.Count == 0,
            $"Expected all {gates.Count} review gates to declare non-empty include globs, but these {missing.Count} did not: {string.Join(", ", missing)}");
    }

    /// <summary>
    ///     The .NET build and test council gates must run for changes to embedded products, review guidance,
    ///     repository config, skills, and fast-loop script, but must not run for dash or general docs.
    /// </summary>
    [Fact]
    public void NetTestPathsIncludeProductsAndReviewGuidance()
    {
        IReadOnlyList<ReviewGate> gates = RepositoryReviewGates();
        ReviewGate buildGate = Assert.Single(gates, gate => gate.Id == "build");
        ReviewGate testGate = Assert.Single(gates, gate => gate.Id == "test");

        string[] shouldMatch =
        [
            "src/KyberWeave.Cli/Program.cs",
            "KyberWeave.sln",
            "products/kyber-squad/agents/conductor.md",
            "REVIEW.md",
            ".kyber-weave/kyber-weave.yml",
            "apm.yml",
            ".apm/skills/kyber-docs-authoring/SKILL.md",
            "scripts/fast-loop.sh"
        ];

        string[] shouldNotMatch =
        [
            "dash/web/src/main.tsx",
            "docs/README.md"
        ];

        foreach (ReviewGate gate in new[] { buildGate, testGate })
        {
            foreach (string path in shouldMatch)
                Assert.True(
                    gate.Include.Any(pattern => PathGlob.IsMatch(pattern, path)),
                    $"Gate '{gate.Id}' include should match '{path}'.");

            foreach (string path in shouldNotMatch)
                Assert.False(
                    gate.Include.Any(pattern => PathGlob.IsMatch(pattern, path)),
                    $"Gate '{gate.Id}' include should NOT match '{path}'.");
        }
    }

    /// <summary>
    ///     The <c>test-policy</c> gate runs <c>--no-build</c>, so every change that selects it must also select
    ///     <c>build</c>: the two declare the same include globs, <c>applies-when</c> and <c>os</c>.
    /// </summary>
    [Fact]
    public void TestPolicyGateIsSelectedExactlyWhereTheBuildGateIs()
    {
        IReadOnlyList<ReviewGate> gates = RepositoryReviewGates();
        ReviewGate buildGate = Assert.Single(gates, gate => gate.Id == "build");
        ReviewGate policyGate = Assert.Single(gates, gate => gate.Id == "test-policy");

        Assert.Equal(buildGate.Include, policyGate.Include);
        Assert.Equivalent(buildGate.AppliesWhen, policyGate.AppliesWhen);
        Assert.Equal(buildGate.Os, policyGate.Os);
    }

    /// <summary>
    ///     The .NET source-only gates (format-whitespace, format-style, duplicates) must match C#
    ///     and solution files, but must not match embedded markdown products, review guidance, general docs, or dash.
    /// </summary>
    [Fact]
    public void NetSourcePathsExcludeProductsAndDocs()
    {
        IReadOnlyList<ReviewGate> gates = RepositoryReviewGates();
        string[] gateIds = ["format-whitespace", "format-style", "duplicates"];

        string[] shouldMatch =
        [
            "src/KyberWeave.Cli/Program.cs",
            "KyberWeave.sln"
        ];

        string[] shouldNotMatch =
        [
            "products/kyber-squad/agents/conductor.md",
            "REVIEW.md",
            "docs/README.md",
            "dash/web/src/main.tsx"
        ];

        foreach (string id in gateIds)
        {
            ReviewGate gate = Assert.Single(gates, g => g.Id == id);

            foreach (string path in shouldMatch)
                Assert.True(
                    gate.Include.Any(pattern => PathGlob.IsMatch(pattern, path)),
                    $"Gate '{gate.Id}' include should match '{path}'.");

            foreach (string path in shouldNotMatch)
                Assert.False(
                    gate.Include.Any(pattern => PathGlob.IsMatch(pattern, path)),
                    $"Gate '{gate.Id}' include should NOT match '{path}'.");
        }
    }

    /// <summary>
    ///     InspectCode is removed from the squad and from this repository's gates (ADR 0075): no review gate
    ///     may run the ReSharper command-line tools, which never exit on the reference machine.
    /// </summary>
    [Fact]
    public void NoReviewGateRunsTheReSharperCommandLineTools()
    {
        IReadOnlyList<ReviewGate> gates = RepositoryReviewGates();

        string[] offenders =
        [
            .. gates
                .Where(gate => gate.Id == "inspectcode" || gate.Run.Contains("jb"))
                .Select(gate => gate.Id)
        ];

        Assert.True(
            offenders.Length == 0,
            "InspectCode was removed from the gates (ADR 0075). Still declared: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     Format analyzers is redundant with the Roslyn build analyzers, so no developer agent
    ///     in review.task-gates may declare a format-analyzers gate.
    /// </summary>
    [Fact]
    public void TaskGatesDoNotContainRedundantFormatAnalyzers()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = RepositoryTaskGates();

        List<string> offenders = [];
        foreach (KeyValuePair<string, IReadOnlyList<ReviewTaskGate>> agentGates in taskGates)
            foreach (ReviewTaskGate gate in agentGates.Value)
                if (gate.Id == "format-analyzers")
                    offenders.Add(agentGates.Key);

        Assert.True(
            offenders.Count == 0,
            $"Expected no agent in review.task-gates to declare 'format-analyzers', but these agents did: {string.Join(", ", offenders)}");
    }

    private static bool HasFlag(IReadOnlyList<string> run, string flag)
    {
        foreach (string element in run)
            if (element.Length > 1 &&
                (element[0] == '-' || element[0] == '/') &&
                element[1..].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static bool IsDotnetCommand(string program)
    {
        string name = program.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPerTaskIsolation(ReviewTaskGate gate)
    {
        for (int i = 0; i < gate.Run.Count; i++)
        {
            string element = gate.Run[i];

            if (element == "--artifacts-path" &&
                i + 1 < gate.Run.Count &&
                gate.Run[i + 1].Contains("{task}", StringComparison.Ordinal))
                return true;

            if ((element.StartsWith("-p:BaseOutputPath=", StringComparison.Ordinal) ||
                 element.StartsWith("-p:BaseIntermediateOutputPath=", StringComparison.Ordinal)) &&
                element.Contains("{task}", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> RepositoryTaskGates()
    {
        KyberWeaveConfigLoadResult result = ConfigFileReader.TryLoad(Corpus.Root);

        Assert.True(result.Success, result.Error);
        return result.Config!.Review.TaskGates;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> RepositoryTechnologyGates()
    {
        KyberWeaveConfigLoadResult result = ConfigFileReader.TryLoad(Corpus.Root);

        Assert.True(result.Success, result.Error);
        return result.Config!.Review.TechnologyGates;
    }

    private static IReadOnlyList<ReviewGate> RepositoryReviewGates()
    {
        KyberWeaveConfigLoadResult result = ConfigFileReader.TryLoad(Corpus.Root);

        Assert.True(result.Success, result.Error);
        return result.Config!.Review.Gates;
    }
}
