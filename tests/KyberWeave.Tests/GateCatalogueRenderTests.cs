using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review.Catalogue;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <see cref="GateCatalogue"/> loads the five embedded family files and renders the <c>review:</c>
/// block <c>docs init</c> writes. Every rendered block must load through
/// <see cref="KyberWeaveConfigLoader"/>, so a bad catalogue entry fails here rather than in a host's
/// first review. Gates aimed at a root the repository does not have are written commented out, so
/// they never run against the wrong directory. Test-dev receives only format and lint gates, so a
/// developer agent's test run is never mixed into another agent's checks.
/// </summary>
/// <remarks>
/// Roots are passed in directly, so a test controls exactly which roots exist. Repository-reading
/// inputs (<c>package.json</c> scripts and <c>global.json</c>) are written into a temp tree, and every
/// declared root directory is created there so the loader's working-directory check can pass.
/// </remarks>
public sealed class GateCatalogueRenderTests
{
    private const string ArtifactsPathValue = ".agent_scratchpad/{task}/artifacts";

    /// <summary>One argument per embedded family, so each family is checked on its own.</summary>
    public static IEnumerable<object[]> FamilyNames() =>
        GateCatalogue.LoadEmbedded().Select(family => new object[] { family.Family });

    /// <summary>Each family, rendered at the repository root and loaded through the config loader, yields a valid review block.</summary>
    /// <remarks>
    /// A family whose block fails validation (a bad argv, an unknown key, a duplicate id) would only surface
    /// when a host ran <c>docs init</c>, so each family is proven loadable by the same loader the CLI uses.
    /// </remarks>
    [Theory]
    [MemberData(nameof(FamilyNames))]
    public void EachFamilyRendersAtTheRepositoryRootAndLoadsThroughTheConfigLoader(string familyName)
    {
        GateCatalogueFamily family = Family(familyName);
        using TempDirectory repository = new();
        Dictionary<string, ProjectRootSet> roots = AtRepositoryRoot([family]);

        RenderedReviewBlock block = GateCatalogue.Render(family.Technologies, roots, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, roots);

        Assert.True(
            config.Review.Gates.Count > 0,
            $"Family '{familyName}' rendered no council gate that loads; its block:{Environment.NewLine}{block.Yaml}");
    }

    /// <summary>Rendering every family together never writes the same council gate id twice.</summary>
    /// <remarks>
    /// Declaring several stacks in one host writes them into one <c>review:</c> block. A duplicate id there
    /// makes every finding and suppression that cites it ambiguous, so uniqueness is checked across families.
    /// </remarks>
    [Fact]
    public void AllFamiliesRenderedTogetherHaveNoDuplicateCouncilId()
    {
        IReadOnlyList<GateCatalogueFamily> families = GateCatalogue.LoadEmbedded();
        string[] declared = families.SelectMany(family => family.Technologies).ToArray();
        using TempDirectory repository = new();
        Dictionary<string, ProjectRootSet> roots = AtRepositoryRoot(families);

        RenderedReviewBlock block = GateCatalogue.Render(declared, roots, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, roots);

        string[] duplicates = config.Review.Gates
            .GroupBy(gate => gate.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.Empty(duplicates);
    }

    /// <summary>Declaring typescript and react writes each shared council gate once, and serves both agents.</summary>
    /// <remarks>
    /// The two technologies share one family. Writing its council gates twice would run every typecheck twice
    /// and make the second copy's findings collide with the first.
    /// </remarks>
    [Fact]
    public void DeclaringTypescriptAndReactWritesEachCouncilGateOnceAndServesBothAgents()
    {
        GateCatalogueFamily family = Family("typescript");
        string[] declared = ["typescript", "react"];
        using TempDirectory repository = new();
        Dictionary<string, ProjectRootSet> roots = AtRepositoryRoot([family]);

        RenderedReviewBlock block = GateCatalogue.Render(declared, roots, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, roots);

        Assert.Single(config.Review.Gates, gate => gate.Id == "typescript-typecheck");
        Assert.Contains("typescript-dev", config.Review.TaskGates.Keys);
        Assert.Contains("react-dev", config.Review.TaskGates.Keys);
    }

    /// <summary>A Rust root outside <c>src-tauri</c> goes to <c>rust-dev</c>, and a <c>src-tauri</c> root goes to <c>tauri-dev</c>.</summary>
    /// <remarks>
    /// Tauri's Rust crate sits under <c>src-tauri</c>. Sending it to <c>rust-dev</c> as well would run its checks
    /// for the wrong agent and give that agent gates for code it does not own.
    /// </remarks>
    [Fact]
    public void RustRootOutsideSrcTauriGoesToRustDevAndTheSrcTauriRootGoesToTauriDev()
    {
        GateCatalogueFamily family = Family("rust");
        ProjectRoot crates = new ProjectRoot("crates/a", "crates/a/Cargo.toml", "crates-a");
        ProjectRoot tauri = new ProjectRoot("app/src-tauri", "app/src-tauri/Cargo.toml", "app-src-tauri");
        Dictionary<string, ProjectRootSet> roots = new(StringComparer.Ordinal)
        {
            [family.Family] = new ProjectRootSet([crates, tauri], false)
        };
        string[] declared = ["rust", "tauri"];
        using TempDirectory repository = new();

        RenderedReviewBlock block = GateCatalogue.Render(declared, roots, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, roots);

        IReadOnlyList<ReviewTaskGate> rustGates = config.Review.TaskGates["rust-dev"];
        IReadOnlyList<ReviewTaskGate> tauriGates = config.Review.TaskGates["tauri-dev"];
        Assert.NotEmpty(rustGates);
        Assert.NotEmpty(tauriGates);
        Assert.All(rustGates, gate => Assert.All(gate.Include, glob =>
            Assert.StartsWith("crates/a/", glob, StringComparison.Ordinal)));
        Assert.All(tauriGates, gate => Assert.All(gate.Include, glob =>
            Assert.StartsWith("app/src-tauri/", glob, StringComparison.Ordinal)));
    }

    /// <summary>The <c>test-dev</c> agent receives the format and lint gates of csharp, and nothing else.</summary>
    /// <remarks>
    /// Test-dev writes tests and must not run a build or a test suite as a per-task gate. Those belong to the
    /// developer agents. A broader set here would slow every test authoring task and run tests before they exist.
    /// </remarks>
    [Fact]
    public void TestDevReceivesOnlyTheFormatAndLintGatesOfTheDeclaredTechnology()
    {
        GateCatalogueFamily family = Family("csharp");
        using TempDirectory repository = new();
        Dictionary<string, ProjectRootSet> roots = AtRepositoryRoot([family]);

        RenderedReviewBlock block = GateCatalogue.Render(["csharp"], roots, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, roots);

        string[] expected = ["csharp-format-analyzers", "csharp-format-style", "csharp-format-whitespace"];
        string[] actual = config.Review.TaskGates["test-dev"]
            .Select(gate => gate.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    /// <summary>Two roots give each council gate a root-suffixed id, and each task gate's include globs sit under its own root.</summary>
    /// <remarks>
    /// Two projects in one repository both have a <c>fmt</c> gate. Without a suffix the second would be a
    /// duplicate id, and without the root prefix a gate would lint files that belong to the other project.
    /// </remarks>
    [Fact]
    public void TwoRootsGiveSuffixedIdsAndIncludeGlobsUnderEachRoot()
    {
        GateCatalogueFamily family = Family("rust");
        ProjectRoot first = new ProjectRoot("crates/a", "crates/a/Cargo.toml", "crates-a");
        ProjectRoot second = new ProjectRoot("crates/b", "crates/b/Cargo.toml", "crates-b");
        Dictionary<string, ProjectRootSet> roots = new(StringComparer.Ordinal)
        {
            [family.Family] = new ProjectRootSet([first, second], false)
        };
        using TempDirectory repository = new();

        RenderedReviewBlock block = GateCatalogue.Render(["rust"], roots, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, roots);

        string[] councilIds = config.Review.Gates.Select(gate => gate.Id).ToArray();
        Assert.Contains("rust-fmt-crates-a", councilIds);
        Assert.Contains("rust-fmt-crates-b", councilIds);
        Assert.DoesNotContain("rust-fmt", councilIds);

        IReadOnlyList<ReviewTaskGate> rustGates = config.Review.TaskGates["rust-dev"];
        Assert.NotEmpty(rustGates);
        foreach (ReviewTaskGate gate in rustGates)
        {
            string prefix;
            if (gate.Id.EndsWith("-crates-a", StringComparison.Ordinal))
            {
                prefix = "crates/a/";
            }
            else
            {
                Assert.True(
                    gate.Id.EndsWith("-crates-b", StringComparison.Ordinal),
                    $"Task gate '{gate.Id}' carries neither root's suffix.");
                prefix = "crates/b/";
            }

            Assert.All(gate.Include, glob => Assert.StartsWith(prefix, glob, StringComparison.Ordinal));
        }
    }

    /// <summary>A family with no root is written commented out, and the comment names the marker it looked for.</summary>
    /// <remarks>
    /// A host that has not created its project yet must still load, and the comment is how it learns what to
    /// point the gate at. An uncommented gate with no root would run a command against a path that does not exist.
    /// </remarks>
    [Fact]
    public void AFamilyWithNoRootHasItsGatesCommentedOutNamingTheMarker()
    {
        GateCatalogueFamily family = Family("csharp");
        string marker = family.Markers[0];
        Dictionary<string, ProjectRootSet> roots = new(StringComparer.Ordinal)
        {
            [family.Family] = new ProjectRootSet(Array.Empty<ProjectRoot>(), false)
        };
        using TempDirectory repository = new();

        RenderedReviewBlock block = GateCatalogue.Render(["csharp"], roots, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, roots);

        Assert.Contains("# - id: csharp-build", block.Yaml, StringComparison.Ordinal);
        Assert.Contains($"no {marker} found", block.Yaml, StringComparison.Ordinal);
        Assert.Contains(block.CommentedOut, entry =>
            entry.GateId == "csharp-build" && entry.Reason.Contains(marker, StringComparison.Ordinal));
        Assert.Empty(config.Review.Gates);
        Assert.DoesNotContain("csharp-dev", config.Review.TaskGates.Keys);
    }

    /// <summary>A gate whose <c>script:</c> condition is unmet is commented out, with the reason.</summary>
    /// <remarks>
    /// The typescript lint gate runs only when the project defines a <c>lint</c> script. Writing it anyway
    /// would fail every review of a project that never set one up. The reason tells the host how to enable it.
    /// </remarks>
    [Fact]
    public void AnUnmetScriptConditionCommentsTheGateOutWithItsReason()
    {
        GateCatalogueFamily family = Family("typescript");
        using TempDirectory repository = new();
        File.WriteAllText(Path.Combine(repository.Path, "package.json"), "{ \"scripts\": {} }");
        Dictionary<string, ProjectRootSet> roots = AtRepositoryRoot([family]);

        RenderedReviewBlock block = GateCatalogue.Render(["typescript"], roots, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, roots);

        Assert.Contains(block.CommentedOut, entry =>
            entry.GateId == "typescript-lint" && entry.Reason.Contains("lint", StringComparison.Ordinal));
        Assert.Contains("# - id: typescript-lint", block.Yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("typescript-lint", config.Review.Gates.Select(gate => gate.Id));
    }

    /// <summary>When a family has more roots than the cap allows, every gate is commented out and the roots are listed.</summary>
    /// <remarks>
    /// Nine near-identical gates per family is noise rather than checks. The host gets the list of roots and an
    /// instruction to choose, instead of a configuration that runs the same command nine times.
    /// </remarks>
    [Fact]
    public void ACappedRootSetCommentsEveryGateOutAndListsTheRoots()
    {
        GateCatalogueFamily family = Family("csharp");
        ProjectRoot[] roots = Enumerable.Range(1, 9)
            .Select(index => new ProjectRoot($"p{index}", $"p{index}/{family.Markers[0]}", $"p{index}"))
            .ToArray();
        Dictionary<string, ProjectRootSet> rootsByFamily = new(StringComparer.Ordinal)
        {
            [family.Family] = new ProjectRootSet(roots, true)
        };
        using TempDirectory repository = new();

        RenderedReviewBlock block = GateCatalogue.Render(["csharp"], rootsByFamily, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, rootsByFamily);

        Assert.Empty(config.Review.Gates);
        Assert.Empty(config.Review.TaskGates);
        Assert.Contains(block.CommentedOut, entry => entry.GateId == "csharp-build");
        foreach (ProjectRoot root in roots)
        {
            Assert.Contains(root.RootDirectory, block.Yaml, StringComparison.Ordinal);
        }
    }

    /// <summary>The csharp build task gate builds into a per-task artifacts path, so parallel workers do not share one output tree.</summary>
    /// <remarks>
    /// Two developer tasks building the same solution into the same <c>bin/obj</c> corrupt each other's output.
    /// The literal <c>{task}</c> token is expanded by the runner per task, so the path must keep it unexpanded here.
    /// </remarks>
    [Fact]
    public void TheCsharpBuildTaskGateDirectsItsOutputUnderThePerTaskPath()
    {
        GateCatalogueFamily family = Family("csharp");
        using TempDirectory repository = new();
        Dictionary<string, ProjectRootSet> roots = AtRepositoryRoot([family]);

        RenderedReviewBlock block = GateCatalogue.Render(["csharp"], roots, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, roots);

        ReviewTaskGate build = config.Review.TaskGates["csharp-dev"].Single(gate => gate.Id == "csharp-build");
        int flag = build.Run.ToList().IndexOf("--artifacts-path");
        Assert.True(
            flag >= 0 && flag < build.Run.Count - 1,
            $"csharp-build argv has no --artifacts-path value: {string.Join(' ', build.Run)}");
        Assert.Equal(ArtifactsPathValue, build.Run[flag + 1]);
    }

    /// <summary>A declared technology with no catalogue family is reported under <c>NoFamily</c>, and the render does not fail.</summary>
    /// <remarks>
    /// <c>azure</c> has no shipped gates yet. A host that declares it must be told so, not have <c>docs init</c>
    /// fail on it, because a declared stack without gates is a valid state.
    /// </remarks>
    [Fact]
    public void ATechnologyWithNoFamilyAppearsInNoFamily()
    {
        using TempDirectory repository = new();
        Dictionary<string, ProjectRootSet> roots = AtRepositoryRoot(GateCatalogue.LoadEmbedded());

        RenderedReviewBlock block = GateCatalogue.Render(["csharp", "azure"], roots, repository.Path);

        Assert.Equal(["azure"], block.NoFamily);
    }

    /// <summary>A <c>global.json</c> that selects Microsoft Testing Platform switches the csharp test gate to <c>--solution</c>.</summary>
    /// <remarks>
    /// The two test runners take different command lines. A VSTest command handed to an MTP project fails with an
    /// unrecognised-option error, so the gate must follow the runner the repository actually declares.
    /// </remarks>
    [Fact]
    public void AGlobalJsonSelectingMtpSwitchesTheCsharpTestGateToSolution()
    {
        GateCatalogueFamily family = Family("csharp");
        using TempDirectory repository = new();
        File.WriteAllText(
            Path.Combine(repository.Path, "global.json"),
            "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }");
        Dictionary<string, ProjectRootSet> roots = AtRepositoryRoot([family]);

        RenderedReviewBlock block = GateCatalogue.Render(["csharp"], roots, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, roots);

        ReviewGate test = config.Review.Gates.Single(gate => gate.Id == "csharp-test");
        Assert.Contains("--solution", test.Run);
    }

    /// <summary>Without an MTP <c>global.json</c>, the csharp test gate uses the VSTest form and does not pass <c>--solution</c>.</summary>
    [Fact]
    public void WithoutAnMtpGlobalJsonTheCsharpTestGateIsTheVsTestForm()
    {
        GateCatalogueFamily family = Family("csharp");
        using TempDirectory repository = new();
        Dictionary<string, ProjectRootSet> roots = AtRepositoryRoot([family]);

        RenderedReviewBlock block = GateCatalogue.Render(["csharp"], roots, repository.Path);
        KyberWeaveConfig config = LoadRendered(repository.Path, block, roots);

        ReviewGate test = config.Review.Gates.Single(gate => gate.Id == "csharp-test");
        Assert.DoesNotContain("--solution", test.Run);
    }

    /// <summary>A family file whose <c>schema</c> is not the supported version is refused at load time.</summary>
    /// <remarks>
    /// A catalogue written for a newer format read by an older binary would be interpreted wrongly and could
    /// write gates that do not mean what their author intended. Refusing it loudly keeps the failure at build time.
    /// </remarks>
    [Fact]
    public void AnUnknownFamilySchemaIsRefused()
    {
        string yaml = """
            schema: 2
            family: future
            technologies: [future]
            roots:
              markers: ["future.toml"]
              nesting: each
            gates: []
            task-gates: {}
            sources: []
            """;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => GateCatalogue.ParseFamily(yaml));
        Assert.Contains("schema", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A <c>when: file:</c> path must stay relative and under the project root.</summary>
    /// <remarks>
    /// The condition is a file-existence probe; an absolute or <c>..</c> path would turn the catalogue into an
    /// oracle for the host filesystem, so the family file is refused at load time.
    /// </remarks>
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../secret")]
    [InlineData("a/../../secret")]
    [InlineData("C:\\secret")]
    public void AWhenFilePathThatEscapesTheRootIsRefused(string path)
    {
        string yaml = $$"""
            schema: 1
            family: probe
            technologies: [probe]
            roots:
              markers: ["probe.toml"]
              nesting: each
            gates:
              - id: check
                role: lint
                when: { file: '{{path}}' }
                run: [probe, check]
            task-gates: {}
            sources: {}
            """;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => GateCatalogue.ParseFamily(yaml));
        Assert.Contains("file", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static GateCatalogueFamily Family(string name)
    {
        return GateCatalogue.LoadEmbedded().Single(family => family.Family == name);
    }

    /// <summary>One synthetic root per family at the repository root, using the family's first marker.</summary>
    private static Dictionary<string, ProjectRootSet> AtRepositoryRoot(IEnumerable<GateCatalogueFamily> families)
    {
        Dictionary<string, ProjectRootSet> roots = new(StringComparer.Ordinal);
        foreach (GateCatalogueFamily family in families)
        {
            ProjectRoot root = new(".", family.Markers[0], "root");
            roots[family.Family] = new ProjectRootSet([root], false);
        }

        return roots;
    }

    /// <summary>
    /// Writes the rendered block as the host's <c>.kyber-weave/kyber-weave.yml</c>, creates every declared
    /// root directory, and loads the repository through <see cref="KyberWeaveConfigLoader"/>. A load failure
    /// throws here, so every test that calls this also proves the block is valid.
    /// </summary>
    private static KyberWeaveConfig LoadRendered(
        string repository,
        RenderedReviewBlock block,
        IReadOnlyDictionary<string, ProjectRootSet> roots)
    {
        foreach (ProjectRoot root in roots.Values.SelectMany(set => set.Roots))
        {
            if (root.RootDirectory != ".")
            {
                Directory.CreateDirectory(Path.Combine(repository, root.RootDirectory));
            }
        }

        string configDirectory = Path.Combine(repository, ".kyber-weave");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, "kyber-weave.yml"), block.Yaml);

        return KyberWeaveConfigLoader.Load(repository);
    }
}
