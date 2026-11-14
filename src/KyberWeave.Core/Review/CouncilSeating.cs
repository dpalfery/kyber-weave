using System.Globalization;
using System.Text;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Core.Review;

/// <summary>
///     Which council seats start at once, which wait for the gates, and which a file-pattern condition the lens
///     catalogue already states rules out; and the packet and dispatch block each seat is sent.
/// </summary>
/// <remarks>
///     Most lenses need only the diff and the changed files, so they start while the gates run. The three that read a
///     gate's output start when the gates finish. A lens is not seated by pattern only where its own Applicability
///     section names the kinds of file it needs; every other applicability question stays with the seat.
/// </remarks>
public static class CouncilSeating
{
    /// <summary>The lenses whose input includes a gate's output, so they start after the gates finish.</summary>
    public static IReadOnlyList<string> AfterGates { get; } =
        ["duplicate-implementation", "static-analysis-triage", "test-adequacy"];

    /// <summary>The lenses that run on <c>review-triage</c>; every other lens runs on <c>review-lens</c>.</summary>
    public static IReadOnlyList<string> TriageLenses { get; } =
        ["dependency-supply-chain", "duplicate-implementation", "static-analysis-triage"];

    private const string DocsOnlyReason = "the change is documentation only (every changed file is *.md)";

    /// <summary>The lenses whose Applicability section says they skip a documentation-only change.</summary>
    private static readonly string[] SkipWhenDocsOnly = ["correctness", "test-adequacy"];

    /// <summary>
    ///     Lenses whose Applicability names the kinds of file they need: each is seated only when a changed path matches
    ///     one of its patterns. The patterns are deliberately wide, because a lens wrongly left out costs a review and a
    ///     lens wrongly seated costs one quick <c>SKIPPED</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Needs, string[] Patterns)> SeatedOnlyWhenAnyMatch =
        new Dictionary<string, (string, string[])>(StringComparer.Ordinal)
        {
            ["dependency-supply-chain"] = (
                "a dependency manifest, lock file, container image or build input",
                [
                    "**/package.json", "**/package-lock.json", "**/npm-shrinkwrap.json", "**/yarn.lock", "**/pnpm-lock.yaml",
                    "**/pnpm-workspace.yaml", "**/bun.lock", "**/bun.lockb", "**/.npmrc", "**/.yarnrc*", "**/*.csproj",
                    "**/*.fsproj", "**/*.vbproj", "**/*.props", "**/*.targets", "**/packages.lock.json", "**/packages.config",
                    "**/nuget.config", "**/global.json", "**/dotnet-tools.json", "**/Cargo.toml", "**/Cargo.lock",
                    "**/go.mod", "**/go.sum", "**/go.work", "**/requirements*.txt", "**/pyproject.toml", "**/poetry.lock",
                    "**/Pipfile", "**/Pipfile.lock", "**/uv.lock", "**/setup.py", "**/setup.cfg", "**/Gemfile",
                    "**/Gemfile.lock", "**/*.gemspec", "**/pom.xml", "**/build.gradle*", "**/settings.gradle*",
                    "**/gradle.properties", "**/gradle/**", "**/composer.json", "**/composer.lock", "**/vcpkg.json",
                    "**/conanfile.*", "**/CMakeLists.txt", "**/Makefile", "**/*.mk", "**/Dockerfile*", "**/*.dockerfile",
                    "**/Containerfile*", "**/docker-compose*", "**/compose.y*ml", "**/apm.yml", "**/*.sh", "**/*.ps1",
                    ".github/**",
                ]),
            ["infra-workflow"] = (
                "infrastructure, a CI or deployment workflow, a database migration or cloud resource configuration",
                [
                    ".github/**", "**/.gitlab-ci.yml", "**/azure-pipelines*", "**/.azure-pipelines/**", "**/.azuredevops/**",
                    "**/Jenkinsfile*", ".circleci/**", "**/bitbucket-pipelines.yml", ".buildkite/**", "**/*.tf",
                    "**/*.tfvars", "**/*.hcl", "**/*.bicep", "**/*.bicepparam", "**/Pulumi*.y*ml", "**/cdk.json",
                    "**/serverless.y*ml", "**/*.sql", "**/*migration*/**", "**/*migration*", "**/Dockerfile*",
                    "**/*.dockerfile", "**/Containerfile*", "**/docker-compose*", "**/compose.y*ml", "**/*k8s*/**",
                    "**/*kubernetes*/**", "**/*helm*/**", "**/charts/**", "**/infra/**", "**/*-infra/**", "**/*.Infra/**", "**/iac/**", "**/*deploy*/**",
                    "**/*terraform*/**", "**/*pulumi*/**", "**/*bicep*/**", "**/*ansible*/**", "**/*cloudformation*/**",
                ]),
            ["test-infrastructure"] = (
                "test support code in a test project or test directory",
                [
                    "**/*test*/**", "**/*Test*/**", "**/*spec*/**", "**/*.test.*", "**/*.spec.*", "**/*_test.*",
                    "**/test_*", "**/*Tests.*", "**/*Test.*", "**/conftest.py", "**/*fixture*", "**/*fixture*/**",
                    "**/jest.config.*", "**/vitest.config.*", "**/playwright*.config.*", "**/karma.conf.*",
                    "**/*.runsettings", "**/xunit.runner.json", "**/pytest.ini", "**/tox.ini",
                ]),
        };

    /// <summary>
    ///     The seating decided from the changed files alone: the seats that start now, the seats that wait for the
    ///     gates, and each lens not seated with its reason. A null changed set seats every lens.
    /// </summary>
    /// <param name="changed">The changed paths, or null when git could not name them.</param>
    public static CouncilSeatPlan Plan(IReadOnlyList<string>? changed)
    {
        List<string> now = [];
        List<string> later = [];
        Dictionary<string, string> notSeated = new(StringComparer.Ordinal);
        bool docsOnly = changed is { Count: > 0 } && changed.All(path => PathGlob.IsMatch("**/*.md", path));

        foreach (string lens in ReviewLensCatalogue.Names)
        {
            if (changed is not null && NotSeatedReason(lens, changed, docsOnly) is { } reason)
            {
                notSeated[lens] = reason;
                continue;
            }

            (AfterGates.Contains(lens, StringComparer.Ordinal) ? later : now).Add(lens);
        }

        return new CouncilSeatPlan(now, later, notSeated);
    }

    /// <summary>
    ///     The gate-proven skips, decided once the gates finished: <c>static-analysis-triage</c> when every gate that ran
    ///     passed, and <c>duplicate-implementation</c> when the duplicates report names no changed file.
    /// </summary>
    /// <param name="gates">The gate report this cycle wrote.</param>
    /// <param name="duplicates">The duplicates report, or null when it could not be produced.</param>
    /// <param name="changed">The changed paths, or null when git could not name them.</param>
    public static IReadOnlyDictionary<string, string> GateProvenSkips(
        GateReport gates, DuplicateReport? duplicates, IReadOnlyList<string>? changed)
    {
        ArgumentNullException.ThrowIfNull(gates);

        Dictionary<string, string> skips = new(StringComparer.Ordinal);
        GateResult[] ran = [.. gates.Gates.Where(gate => !gate.NotApplicable)];
        if (ran.Length > 0 && ran.All(gate => gate.Passed))
            skips["static-analysis-triage"] = $"gates {string.Join(", ", ran.Select(gate => gate.Id))} passed";

        if (duplicates is { IndexAvailable: true } && changed is not null)
        {
            HashSet<string> paths = new(changed.Select(Normalize), StringComparer.Ordinal);
            if (!duplicates.Clusters.Any(cluster => cluster.Members.Any(member => paths.Contains(Normalize(member.File)))))
                skips["duplicate-implementation"] = "no duplicate cluster names a changed file";
        }

        return skips;
    }

    /// <summary>The agent a lens's seat runs on.</summary>
    public static string Runner(string lens) =>
        TriageLenses.Contains(lens, StringComparer.Ordinal) ? "review-triage" : "review-lens";

    /// <summary>
    ///     One seat's packet: the lens charter itself, the evidence, the tree to ask the index about, the strictness and
    ///     where the review guidance is, so a seat opens nothing else to start.
    /// </summary>
    public static string Packet(CouncilSeatContext context, string lens)
    {
        ArgumentNullException.ThrowIfNull(context);

        StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Seat packet: {lens}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Evidence: {context.EvidencePath}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- projectPath: {context.Tree}");
        if (!context.IndexReady)
            text.AppendLine("- Index: unavailable for this tree, so read files directly and record `Index fallback: projectPath: not-indexed`.");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Strictness: {context.Strictness} (min-confidence {context.MinConfidence.ToString(CultureInfo.InvariantCulture)})");
        text.AppendLine(context.GuidancePath is null
            ? "- Review guidance: none declared, or the file is missing. Say so in your output."
            : $"- Review guidance: {context.GuidancePath}. Apply its ground rules and what-not-to-flag, and the sections its lens and technology maps name for your lens and the changed files.");
        if (string.Equals(lens, "duplicate-implementation", StringComparison.Ordinal) && context.DuplicatesPath is not null)
            text.AppendLine(CultureInfo.InvariantCulture, $"- Duplicates report: {context.DuplicatesPath}");
        text.AppendLine();
        text.AppendLine("## Lens charter");
        text.AppendLine();
        text.AppendLine(ArbiterSquadCatalog.EmbeddedLensText(lens)?.Trim()
                        ?? $"(No embedded charter for {lens}: read `references/lenses/{lens}.md` in the code-review skill.)");
        return text.ToString();
    }

    /// <summary>
    ///     The dispatch block for one seat: the routing header, then the packet path and, in hand-back mode, the seat
    ///     file the seat writes. The reviewer sends the lines between the markers unchanged.
    /// </summary>
    public static string DispatchBlock(string lens, string reviewRun, string packetPath, string? seatFile)
    {
        StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture, $"--- seat {lens} agent={Runner(lens)} ---");
        text.AppendLine("KYBER-ARBITER: true");
        text.AppendLine(CultureInfo.InvariantCulture, $"LENS: {lens}");
        text.AppendLine(CultureInfo.InvariantCulture, $"REVIEW_RUN: {reviewRun}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Packet: {packetPath}");
        if (seatFile is not null)
            text.AppendLine(CultureInfo.InvariantCulture, $"Seat file: {seatFile}");
        text.AppendLine("--- end ---");
        return text.ToString();
    }

    private static string? NotSeatedReason(string lens, IReadOnlyList<string> changed, bool docsOnly)
    {
        if (docsOnly && SkipWhenDocsOnly.Contains(lens, StringComparer.Ordinal))
            return DocsOnlyReason;

        if (!SeatedOnlyWhenAnyMatch.TryGetValue(lens, out (string Needs, string[] Patterns) rule))
            return null;

        return changed.Any(path => rule.Patterns.Any(pattern => PathGlob.IsMatch(pattern, path)))
            ? null
            : $"no changed file is {rule.Needs} (catalogue applicability, matched on the changed-file list)";
    }

    private static string Normalize(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}

/// <summary>The council's seating decided from the changed files.</summary>
/// <param name="Now">The lenses seated at once, while the gates run.</param>
/// <param name="AfterGates">The lenses seated when the gates finish, unless the gate run proves them unneeded.</param>
/// <param name="NotSeated">Each lens not seated, with the reason <c>--not-seated</c> records.</param>
public sealed record CouncilSeatPlan(
    IReadOnlyList<string> Now, IReadOnlyList<string> AfterGates, IReadOnlyDictionary<string, string> NotSeated);

/// <summary>What every seat packet of one review cycle shares.</summary>
/// <param name="Tree">The work tree under review, passed to the index as <c>projectPath</c>.</param>
/// <param name="EvidencePath">The cycle's <c>evidence.md</c>.</param>
/// <param name="IndexReady">Whether the tree's CodeGraph index is ready.</param>
/// <param name="Strictness">The effective strictness preset name.</param>
/// <param name="MinConfidence">The effective min-confidence.</param>
/// <param name="GuidancePath">The review guidance file, or null when none is declared or it is missing.</param>
/// <param name="DuplicatesPath">The duplicates report the gate run writes.</param>
public sealed record CouncilSeatContext(
    string Tree,
    string EvidencePath,
    bool IndexReady,
    string Strictness,
    int MinConfidence,
    string? GuidancePath,
    string? DuplicatesPath);
