// ASSUMED API (RED test: implementers must provide these names, or update this comment and the
// tests together):
//
//   KyberWeaveConfigLoader.LoadFromYaml(string) -> KyberWeaveConfig
//   KyberWeaveConfigLoader.TryLoad(string repoRoot) -> KyberWeaveConfigLoadResult
//   KyberWeaveConfig.Review.TaskGates
//       : IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>>
//       key   = agent slug ("csharp-dev"); value = gates in declaration order; empty when absent
//   ReviewTaskGate (sealed record, namespace KyberWeave.Core.Configuration):
//       string Id
//       IReadOnlyList<string> Include   // glob patterns
//       string? Os                      // "windows" | "unix" | null
//       IReadOnlyList<string> Run       // argv; "{changed}" / "{task}" kept literal as whole elements
//       IReadOnlyList<string> Fix       // argv, same token rules as Run
//       bool Blocking                   // default true
//       int TimeoutSeconds              // default 900
//   Rejections are YamlException. Every message names the agent slug and, for gate-level faults,
//   the gate id, plus the offending field or value. Unknown {tokens} also carry the nearest known
//   token ("{changed}" or "{task}") in the message.
//   Known tokens: changed, task.

using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>review.task-gates</c> section decides what a developer agent runs against its own
/// working tree before a reviewer sees the change. A malformed gate is refused where the operator
/// writes it, because a gate that only fails when it runs would surface as a mystery in the middle
/// of someone else's task.
/// </summary>
public sealed class ReviewTaskGatesConfigTests
{
    private const string FormatRun =
        "run: [dotnet, format, KyberWeave.sln, whitespace, --verify-no-changes, --include, \"{changed}\"]";

    private const string FormatFix =
        "fix: [dotnet, format, KyberWeave.sln, whitespace, --include, \"{changed}\"]";

    private static IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> TaskGates(string yaml)
    {
        return KyberWeaveConfigLoader.LoadFromYaml(yaml).Review.TaskGates;
    }

    /// <summary>
    /// Builds a document with a single gate under <paramref name="agent"/>. The first field is the
    /// gate's leading key (<c>id: ...</c>); each further field is a sibling key of the same gate.
    /// </summary>
    private static string OneGate(string agent, params string[] fields)
    {
        return "review:\n  task-gates:\n    " + agent + ":\n      - " + string.Join("\n        ", fields) + "\n";
    }

    /// <summary>
    /// Builds a valid gate whose <paramref name="field"/> (<c>run</c> or <c>fix</c>) carries the
    /// given argv, with the other argv field kept valid so only the tested field can be at fault.
    /// </summary>
    private static string GateWithArgv(string field, string argv)
    {
        return field == "run"
            ? OneGate("csharp-dev", "id: format-whitespace", "run: [" + argv + "]", FormatFix)
            : OneGate("csharp-dev", "id: format-whitespace", FormatRun, "fix: [" + argv + "]");
    }

    /// <summary>
    /// Asserts the load is refused and that the rejection names each fragment. Naming the offender
    /// is what lets an operator find the line to fix without reading the whole file again.
    /// </summary>
    private static void AssertRejected(string yaml, params string[] mustName)
    {
        YamlException ex = Assert.ThrowsAny<YamlException>(() => KyberWeaveConfigLoader.LoadFromYaml(yaml));
        foreach (string fragment in mustName)
        {
            Assert.True(
                ex.Message.Contains(fragment, StringComparison.Ordinal),
                $"Expected the rejection to name '{fragment}', but it said: {ex.Message}");
        }
    }

    [Fact]
    public void AnAbsentTaskGatesSectionDeclaresNoTaskGates()
    {
        Assert.Empty(TaskGates("ontology:\n  docs-root: docs\n"));
    }

    [Fact]
    public void TaskGatesAreKeyedByAgentAndReadInDeclarationOrder()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = TaskGates("""
            review:
              task-gates:
                csharp-dev:
                  - id: format-whitespace
                    run: [dotnet, format, KyberWeave.sln, whitespace, --verify-no-changes]
                    fix: [dotnet, format, KyberWeave.sln, whitespace]
                  - id: cleanup-code
                    run: [dotnet, jb, cleanupcode]
                    fix: [dotnet, jb, cleanupcode]
                dal-dev:
                  - id: format-whitespace
                    run: [dotnet, format, KyberWeave.sln, whitespace, --verify-no-changes]
                    fix: [dotnet, format, KyberWeave.sln, whitespace]
            """);

        Assert.Equal(["csharp-dev", "dal-dev"], taskGates.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["format-whitespace", "cleanup-code"], taskGates["csharp-dev"].Select(g => g.Id));
        Assert.Equal(["format-whitespace"], taskGates["dal-dev"].Select(g => g.Id));
    }

    /// <summary>
    /// A gate that omits <c>blocking</c> and <c>timeout</c> must still block and must still be
    /// bounded; silently defaulting either to something else would change what a failing gate means.
    /// </summary>
    [Fact]
    public void AGateOmittingBlockingAndTimeoutBlocksWithAFifteenMinuteLimit()
    {
        ReviewTaskGate gate = Assert.Single(
            TaskGates(OneGate("csharp-dev", "id: format-whitespace", FormatRun, FormatFix))["csharp-dev"]);

        Assert.True(gate.Blocking);
        Assert.Equal(900, gate.TimeoutSeconds);
    }

    [Fact]
    public void OsIncludeFixAndRunAreParsedAsDeclared()
    {
        ReviewTaskGate gate = Assert.Single(TaskGates(OneGate(
            "csharp-dev",
            "id: format-whitespace",
            "os: unix",
            "include: [\"**/*.cs\"]",
            "blocking: false",
            "timeout: 600",
            FormatRun,
            FormatFix))["csharp-dev"]);

        Assert.Equal("format-whitespace", gate.Id);
        Assert.Equal("unix", gate.Os);
        Assert.Equal(["**/*.cs"], gate.Include);
        Assert.False(gate.Blocking);
        Assert.Equal(600, gate.TimeoutSeconds);
        Assert.Equal(
            ["dotnet", "format", "KyberWeave.sln", "whitespace", "--verify-no-changes", "--include", "{changed}"],
            gate.Run);
        Assert.Equal(
            ["dotnet", "format", "KyberWeave.sln", "whitespace", "--include", "{changed}"],
            gate.Fix);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("unix")]
    public void AnOsOfWindowsOrUnixIsAccepted(string os)
    {
        ReviewTaskGate gate = Assert.Single(TaskGates(OneGate(
            "csharp-dev",
            "id: format-whitespace",
            "os: " + os,
            FormatRun,
            FormatFix))["csharp-dev"]);

        Assert.Equal(os, gate.Os);
    }

    /// <summary>
    /// The anchor exists so one gate definition can be shared; the alias must resolve to the same
    /// argv, not to an empty or partially read gate.
    /// </summary>
    [Fact]
    public void AnAnchoredGateAliasedByAnotherAgentResolvesToTheSameGate()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = TaskGates("""
            review:
              task-gates:
                csharp-dev:
                  - &fmt-whitespace
                    id: format-whitespace
                    include: ["**/*.cs"]
                    run: [dotnet, format, KyberWeave.sln, whitespace, --verify-no-changes, --include, "{changed}"]
                    fix: [dotnet, format, KyberWeave.sln, whitespace, --include, "{changed}"]
                dal-dev: [*fmt-whitespace]
            """);

        ReviewTaskGate original = Assert.Single(taskGates["csharp-dev"]);
        ReviewTaskGate aliased = Assert.Single(taskGates["dal-dev"]);

        Assert.Equal("format-whitespace", aliased.Id);
        Assert.Equal(original.Run, aliased.Run);
        Assert.Equal(original.Fix, aliased.Fix);
        Assert.Equal(original.Include, aliased.Include);
    }

    /// <summary>
    /// Gate ids are only required to be unique within an agent, because each agent's gates are
    /// reported as that agent's list. Refusing the same id under two agents would forbid the most
    /// natural shared shape.
    /// </summary>
    [Fact]
    public void TheSameGateIdUnderTwoDifferentAgentsIsAccepted()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = TaskGates(
            "review:\n  task-gates:\n" +
            "    csharp-dev:\n      - id: format-whitespace\n        run: [a]\n        fix: [a]\n" +
            "    dal-dev:\n      - id: format-whitespace\n        run: [a]\n        fix: [a]\n");

        Assert.Equal(["csharp-dev", "dal-dev"], taskGates.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ReviewGatesStillParseAlongsideTaskGates()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
                                                                      review:
                                                                        gates:
                                                                          - id: build
                                                                            run: [dotnet, build, -c, Release]
                                                                        task-gates:
                                                                          csharp-dev:
                                                                            - id: format-whitespace
                                                                              run: [dotnet, format, KyberWeave.sln, whitespace, --verify-no-changes]
                                                                              fix: [dotnet, format, KyberWeave.sln, whitespace]
                                                                      """);

        Assert.Equal(["build"], config.Review.Gates.Select(g => g.Id));
        Assert.Equal(["dotnet", "build", "-c", "Release"], config.Review.Gates[0].Run);
        Assert.True(config.Review.Gates[0].Blocking);
        Assert.Equal(900, config.Review.Gates[0].TimeoutSeconds);
        Assert.Equal(["csharp-dev"], config.Review.TaskGates.Keys);
    }

    [Theory]
    [InlineData("Csharp-Dev")]
    [InlineData("csharp_dev")]
    [InlineData("-dev")]
    public void AnAgentNameThatIsNotALookupNameIsRejected(string agent)
    {
        AssertRejected(OneGate(agent, "id: format-whitespace", FormatRun, FormatFix), "task-gates", agent);
    }

    [Fact]
    public void ADuplicateGateIdWithinOneAgentIsRejected()
    {
        AssertRejected(
            "review:\n  task-gates:\n    csharp-dev:\n" +
            "      - id: format-whitespace\n        run: [a]\n        fix: [a]\n" +
            "      - id: format-whitespace\n        run: [b]\n        fix: [b]\n",
            "csharp-dev",
            "format-whitespace");
    }

    [Fact]
    public void AGateWithoutARunCommandIsRejected()
    {
        AssertRejected(
            OneGate("csharp-dev", "id: format-whitespace", FormatFix),
            "csharp-dev",
            "format-whitespace",
            "run");
    }

    [Fact]
    public void AGateWithAnEmptyRunListIsRejected()
    {
        AssertRejected(
            OneGate("csharp-dev", "id: format-whitespace", "run: []", FormatFix),
            "csharp-dev",
            "format-whitespace",
            "run");
    }

    [Theory]
    [InlineData("run", "dotnet, \"\"")]
    [InlineData("run", "dotnet, \"   \"")]
    [InlineData("fix", "dotnet, \"\"")]
    public void AnEmptyArgvElementInRunOrFixIsRejected(string field, string argv)
    {
        AssertRejected(GateWithArgv(field, argv), "csharp-dev", "format-whitespace", field);
    }

    /// <summary>
    /// <c>{changed}</c> is substituted with the changed file list only when it is a whole argument.
    /// Embedded in a larger argument, the substitution would splice paths into a string the runner
    /// cannot reason about, so it is refused in both <c>run</c> and <c>fix</c>.
    /// </summary>
    [Theory]
    [InlineData("run")]
    [InlineData("fix")]
    public void AChangedTokenEmbeddedInALargerArgvElementIsRejected(string field)
    {
        AssertRejected(GateWithArgv(field, "dotnet, \"--include={changed}\""), "csharp-dev", "format-whitespace", field,
            "{changed}");
    }

    [Theory]
    [InlineData("run")]
    [InlineData("fix")]
    public void AnUnknownBraceTokenIsRejectedWithTheNearestKnownToken(string field)
    {
        AssertRejected(GateWithArgv(field, "dotnet, \"{chngd}\""), "csharp-dev", "format-whitespace", "{chngd}",
            "{changed}");
    }

    [Fact]
    public void WholeChangedAndTaskTokensAreKeptLiteralForTheRunnerToExpand()
    {
        ReviewTaskGate gate = Assert.Single(TaskGates(OneGate(
            "csharp-dev",
            "id: format-whitespace",
            "run: [dotnet, format, \"{changed}\", \"{task}\"]",
            FormatFix))["csharp-dev"]);

        Assert.Equal(["dotnet", "format", "{changed}", "{task}"], gate.Run);
    }

    /// <summary>
    /// The program name is never expanded. A token there would let a changed file or a task slug choose
    /// what executes, so <c>run</c> and <c>fix</c> must name a literal program and take tokens only as arguments.
    /// </summary>
    [Theory]
    [InlineData("run", "\"{task}\", \"x\"")]
    [InlineData("run", "\"{changed}\"")]
    [InlineData("run", "\"tool-{task}\"")]
    [InlineData("fix", "\"{task}\"")]
    [InlineData("fix", "\"{changed}\"")]
    public void ATokenInTheProgramNameIsRejectedSoTheProgramIsALiteral(string field, string argv)
    {
        AssertRejected(GateWithArgv(field, argv), "csharp-dev", "format-whitespace", field, "program");
    }

    [Fact]
    public void AnOsOtherThanWindowsOrUnixIsRejected()
    {
        AssertRejected(
            OneGate("csharp-dev", "id: format-whitespace", "os: bsd", FormatRun, FormatFix),
            "csharp-dev",
            "format-whitespace",
            "os",
            "bsd");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void ATimeoutOfZeroOrLessIsRejected(string seconds)
    {
        AssertRejected(
            OneGate("csharp-dev", "id: format-whitespace", "timeout: " + seconds, FormatRun, FormatFix),
            "csharp-dev",
            "format-whitespace",
            "timeout");
    }

    /// <summary>
    /// The checked-in configuration is what this repository's own developer agents run. Declaring
    /// the gates there is the point of the section, so the set is pinned: a missing agent is a
    /// silent gap, and an extra one is a gate nobody reviewed for that role.
    /// </summary>
    [Fact]
    public void RepositoryConfigDeclaresDeveloperTaskGates()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = RepositoryTaskGates();

        string[] developers =
        [
            "csharp-dev",
            "dal-dev",
            "react-dev",
            "maui-dev",
            "tauri-dev",
            "test-dev",
            "pulumi-dev",
            "sql-database-architect",
            "github-devops"
        ];

        Assert.Equal(developers.Order(StringComparer.Ordinal), taskGates.Keys.Order(StringComparer.Ordinal));
        Assert.All(developers, agent => Assert.NotEmpty(taskGates[agent]));
    }

    /// <summary>
    /// Python developers and the non-developer roles do not run a fix-and-verify gate against their
    /// own change, so declaring one for them would make the task gate run where it was never
    /// reviewed.
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
            "docs-dev",
            "code-reviewer"
        ];

        Assert.All(notDevelopers, agent => Assert.DoesNotContain(agent, taskGates.Keys));
    }

    /// <summary>
    /// A change to the solution file, a NuGet.config, or an .editorconfig changes what the build
    /// compiles and how the analyzers read it, so the build gate must run for it. The format gates
    /// must not match those files: <c>dotnet format</c> given a solution in <c>--include</c> checks
    /// nothing and exits 0, so a format gate that matched the solution would pass without checking.
    /// </summary>
    [Fact]
    public void SolutionAndBuildConfigChangesRunTheBuildGateButNotTheFormatGates()
    {
        IReadOnlyList<ReviewTaskGate> gates = RepositoryTaskGates()["csharp-dev"];
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
        {
            Assert.True(
                build.Include.Any(pattern => PathGlob.IsMatch(pattern, path)),
                $"dotnet-build should run for a change to '{path}'.");
        }

        foreach (ReviewTaskGate format in gates.Where(gate => gate.Id.StartsWith("format-", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(
                buildInputs,
                path => format.Include.Any(pattern => PathGlob.IsMatch(pattern, path)));
        }
    }

    /// <summary>
    /// T5 RED (issue #386 item E): concurrent workers share nothing when no MSBuild node is
    /// reused and no Roslyn server is shared across them. Every <c>dotnet-build</c> task gate
    /// therefore carries <c>-nr:false -p:UseSharedCompilation=false</c>. Only the build gates
    /// are pinned: the format gates do not compile, so node reuse and shared compilation do not
    /// apply to them. The repository gates below name neither flag yet, so this fails until T5
    /// GREEN adds both flags to the task-gate build argv.
    /// </summary>
    [Fact]
    public void DotnetBuildTaskGatesCarryNoNodeReuseAndNoSharedCompilation()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = RepositoryTaskGates();

        List<string> offenders = [];
        int total = 0;
        foreach (KeyValuePair<string, IReadOnlyList<ReviewTaskGate>> entry in taskGates)
        {
            foreach (ReviewTaskGate gate in entry.Value)
            {
                if (gate.Id != "dotnet-build")
                    continue;

                total++;
                if (!gate.Run.Contains("-nr:false") || !gate.Run.Contains("-p:UseSharedCompilation=false"))
                    offenders.Add($"{entry.Key}/{gate.Id}: [{string.Join(", ", gate.Run)}]");
            }
        }

        Assert.True(total > 0, "Expected at least one dotnet-build task gate in the repository config.");
        Assert.True(offenders.Count == 0,
            "T5 RED (issue #386 item E): these dotnet-build task gates lack -nr:false or -p:UseSharedCompilation=false:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    /// The review <c>build</c> gate compiles the same solution the task gates compile, so it
    /// carries the same two flags. The checked-in gate below names neither yet.
    /// </summary>
    [Fact]
    public void ReviewBuildGateCarriesNoNodeReuseAndNoSharedCompilation()
    {
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(KyberWeaveTestPaths.ToolRoot);

        Assert.True(result.Success, result.Error);
        ReviewGate build = Assert.Single(result.Config!.Review.Gates, gate => gate.Id == "build");

        Assert.Contains("-nr:false", build.Run);
        Assert.Contains("-p:UseSharedCompilation=false", build.Run);
    }

    /// <summary>
    /// T5 RED (issue #386 item E): every task gate that runs <c>dotnet</c> isolates its build
    /// output under a per-task path, so two agents building at once never share one
    /// <c>obj/</c> tree. A gate qualifies with <c>--artifacts-path</c> naming <c>{task}</c>, or
    /// with <c>-p:BaseOutputPath=</c> / <c>-p:BaseIntermediateOutputPath=</c> naming
    /// <c>{task}</c> where the command takes no <c>--artifacts-path</c>. The format gates below
    /// name neither yet, so this fails until T5 GREEN isolates them.
    /// </summary>
    [Fact]
    public void EveryDotnetTaskGateUsesAPerTaskArtifactsPath()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = RepositoryTaskGates();

        List<string> offenders = [];
        int total = 0;
        foreach (KeyValuePair<string, IReadOnlyList<ReviewTaskGate>> entry in taskGates)
        {
            foreach (ReviewTaskGate gate in entry.Value)
            {
                if (gate.Run.Count == 0 || !IsDotnetCommand(gate.Run[0]))
                    continue;

                total++;
                if (!HasPerTaskIsolation(gate))
                    offenders.Add($"{entry.Key}/{gate.Id}: [{string.Join(", ", gate.Run)}]");
            }
        }

        Assert.True(total > 0, "Expected at least one dotnet task gate in the repository config.");
        Assert.True(
            offenders.Count == 0,
            "T5 RED (issue #386 item E): these dotnet task gates share one obj/ tree — " +
            "give each a per-task --artifacts-path or -p:Base*Path naming {task}:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    /// The build gate already isolates: it writes under the task scratchpad with the
    /// <c>{task}</c> token the runner expands per task. This pins that contract so the flag
    /// work above cannot regress it.
    /// </summary>
    [Fact]
    public void DotnetBuildTaskGatesIsolateUnderTheTaskScratchpad()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> taskGates = RepositoryTaskGates();

        List<string> offenders = [];
        foreach (KeyValuePair<string, IReadOnlyList<ReviewTaskGate>> entry in taskGates)
        {
            foreach (ReviewTaskGate gate in entry.Value)
            {
                if (gate.Id != "dotnet-build")
                    continue;

                int artifacts = -1;
                for (int i = 0; i < gate.Run.Count; i++)
                {
                    if (gate.Run[i] == "--artifacts-path")
                    {
                        artifacts = i;
                        break;
                    }
                }

                bool isolated = artifacts >= 0 &&
                                artifacts + 1 < gate.Run.Count &&
                                gate.Run[artifacts + 1].Contains("{task}", StringComparison.Ordinal) &&
                                gate.Run[artifacts + 1].Contains(".agent_scratchpad/", StringComparison.Ordinal);

                if (!isolated)
                    offenders.Add($"{entry.Key}/{gate.Id}: [{string.Join(", ", gate.Run)}]");
            }
        }

        Assert.True(offenders.Count == 0,
            "dotnet-build task gates must keep --artifacts-path under .agent_scratchpad/{task}:\n" +
            string.Join("\n", offenders));
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
            {
                return true;
            }

            if ((element.StartsWith("-p:BaseOutputPath=", StringComparison.Ordinal) ||
                 element.StartsWith("-p:BaseIntermediateOutputPath=", StringComparison.Ordinal)) &&
                element.Contains("{task}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> RepositoryTaskGates()
    {
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(KyberWeaveTestPaths.ToolRoot);

        Assert.True(result.Success, result.Error);
        return result.Config!.Review.TaskGates;
    }
}
