using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Configuration;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Review.Catalogue;

/// <summary>
/// Loads the embedded gate catalogue families and renders the host <c>review:</c> block
/// <c>docs init</c> writes.
/// </summary>
/// <remarks>
/// The families live next to the coding standards they belong to and travel inside the binary,
/// so rendering needs no filesystem paths. The block is emitted as text rather than round-tripped
/// through a YAML emitter: the host owns the text afterwards, edits it by hand, and re-running
/// <c>docs init</c> never touches it, so commented-out gates stay commented in a form the host can
/// uncomment in place. YAML anchors are not used for the same reason: each entry must be editable
/// on its own.
/// </remarks>
public static class GateCatalogue
{
    private static readonly string[] FamilyOrder = ["csharp", "typescript", "rust", "go", "python"];

    private static readonly string[] AgentOrder =
    [
        "csharp-dev", "dal-dev", "maui-dev", "pulumi-dev",
        "typescript-dev", "react-dev", "rust-dev", "tauri-dev",
        "go-dev", "python-dev", "test-dev"
    ];

    private static readonly Dictionary<string, string> AgentTechnology = new(StringComparer.Ordinal)
    {
        ["csharp-dev"] = "csharp",
        ["dal-dev"] = "csharp",
        ["maui-dev"] = "csharp",
        ["pulumi-dev"] = "csharp",
        ["typescript-dev"] = "typescript",
        ["react-dev"] = "react",
        ["rust-dev"] = "rust",
        ["tauri-dev"] = "tauri",
        ["go-dev"] = "go",
        ["python-dev"] = "python"
    };

    private static readonly string[] KnownRoles = ["format", "lint", "typecheck", "build", "test"];

    private const string RunnerVstest = "vstest";
    private const string RunnerMtp = "mtp";
    private const string MtpRunnerName = "Microsoft.Testing.Platform";

    /// <summary>Loads the five embedded family files, in catalogue family order.</summary>
    public static IReadOnlyList<GateCatalogueFamily> LoadEmbedded()
    {
        Assembly assembly = typeof(GateCatalogue).Assembly;
        List<GateCatalogueFamily> families = new(FamilyOrder.Length);
        foreach (string name in FamilyOrder)
        {
            using Stream? stream = assembly.GetManifestResourceStream($"Gates.{name}.yml");
            if (stream is null)
                throw new InvalidDataException($"Embedded catalogue family 'Gates.{name}.yml' is missing.");

            using StreamReader reader = new(stream, Encoding.UTF8);
            families.Add(ParseFamily(reader.ReadToEnd()));
        }

        return families;
    }

    /// <summary>
    /// Parses one family file. Anything other than schema <c>1</c> is refused, so a catalogue
    /// written for a newer format fails at load time instead of writing gates it does not mean.
    /// </summary>
    public static GateCatalogueFamily ParseFamily(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        YamlMappingNode root;
        try
        {
            YamlStream stream = new();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                throw new InvalidDataException("Gate catalogue family must be a YAML mapping at the document root.");
            }

            root = mapping;
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Gate catalogue family is not valid YAML: {ex.Message}", ex);
        }

        string family = RequiredScalar(root, "<document>", "family");
        if (!ConfigSlug.IsValid(family))
            throw new InvalidDataException($"Gate catalogue family '{family}' is not a valid lookup name.");

        string? schema = OptionalScalar(root, "schema");
        if (!string.Equals(schema, "1", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Gate catalogue family '{family}' declares unsupported schema '{schema ?? "<missing>"}'; this binary reads schema 1.");
        }

        List<string> technologies = RequiredStrings(root, "technologies");
        YamlMappingNode roots = RequiredMapping(root, "roots");
        List<string> markers = RequiredStrings(roots, "markers");

        string nestingText = RequiredScalar(roots, "roots", "nesting");
        ProjectRootNesting nesting = nestingText switch
        {
            "outermost" => ProjectRootNesting.Outermost,
            "each" => ProjectRootNesting.Each,
            _ => throw new InvalidDataException(
                $"Gate catalogue family '{family}' roots.nesting '{nestingText}' is not recognised. " +
                "Use 'outermost' or 'each'.")
        };

        bool pair = OptionalBoolean(roots, "pair", family);
        List<string>? fallback = OptionalStrings(roots, "fallback");

        List<FamilyGate> gates = ParseGateList(root, family, "gates", false);
        List<FamilyGate> taskGates = ParseGateList(root, family, "task-gates", true);

        return new GateCatalogueFamily(family, technologies, markers, nesting, pair, fallback, gates, taskGates);
    }

    /// <summary>
    /// Renders the <c>review:</c> block for the declared technologies and the roots each family
    /// was detected at, with repository inputs (<c>global.json</c>, per-root <c>package.json</c>)
    /// read from <paramref name="repositoryRoot"/>.
    /// </summary>
    /// <remarks>
    /// Conditions are evaluated per root, because two roots of one repository can differ: one
    /// <c>package.json</c> may define a <c>lint</c> script while another does not. Entries that
    /// share an id pick one form per root this way; the test runner form follows the repository
    /// <c>global.json</c>, which is repository-wide.
    /// </remarks>
    public static RenderedReviewBlock Render(
        IReadOnlyList<string> declaredTechnologies,
        IReadOnlyDictionary<string, ProjectRootSet> rootsByFamily,
        string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(declaredTechnologies);
        ArgumentNullException.ThrowIfNull(rootsByFamily);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        IReadOnlyList<GateCatalogueFamily> families = LoadEmbedded();
        HashSet<string> declared = new(declaredTechnologies, StringComparer.Ordinal);
        string runner = DetectRunner(repositoryRoot);
        Renderer renderer = new();

        foreach (GateCatalogueFamily family in families)
        {
            List<string> served = family.Technologies.Where(declared.Contains).ToList();
            if (served.Count == 0)
                continue;

            ProjectRootSet set =
                rootsByFamily.TryGetValue(family.Family, out ProjectRootSet? found) && found is not null
                    ? found
                    : new ProjectRootSet([], false);
            RenderFamily(renderer, family, served, set, declared, runner, repositoryRoot);
        }

        RenderTestDev(renderer);

        List<string> noFamily = declaredTechnologies
            .Where(technology => !families.Any(family =>
                family.Technologies.Contains(technology, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new RenderedReviewBlock(BuildYaml(renderer), renderer.Written, renderer.CommentedOut, noFamily);
    }

    private static void RenderFamily(
        Renderer renderer,
        GateCatalogueFamily family,
        List<string> served,
        ProjectRootSet set,
        HashSet<string> declared,
        string runner,
        string repositoryRoot)
    {
        string technologies = string.Join(", ", served);
        if (set.Capped)
        {
            RenderCapped(renderer, family, technologies, set);
            return;
        }

        int rootCount = set.Roots.Count;
        List<RootContext> contexts = rootCount == 0
            ? [new RootContext(".", family.Markers[0], string.Empty, ".", true)]
            : set.Roots
                .Select(root => new RootContext(
                    root.RootDirectory,
                    root.MarkerPath,
                    rootCount > 1 ? "-" + root.Slug : string.Empty,
                    root.RootDirectory,
                    false))
                .ToList();

        Dictionary<string, HashSet<string>> scriptsByRoot = new(StringComparer.Ordinal);

        HashSet<string> ScriptsFor(string rootDirectory)
        {
            if (!scriptsByRoot.TryGetValue(rootDirectory, out HashSet<string>? scripts))
            {
                scripts = ReadPackageScripts(repositoryRoot, rootDirectory);
                scriptsByRoot[rootDirectory] = scripts;
            }

            return scripts;
        }

        foreach (IGrouping<string, FamilyGate> group in family.Gates.GroupBy(gate => gate.Id, StringComparer.Ordinal))
        {
            foreach (RootContext target in GroupTargets(group, contexts))
                RenderCouncilEntry(renderer, family, served, technologies, group, target, ScriptsFor, runner,
                    repositoryRoot);
        }

        foreach (IGrouping<string, FamilyGate> group in family.TaskGates.GroupBy(gate => gate.Id,
                     StringComparer.Ordinal))
        {
            foreach (RootContext target in GroupTargets(group, contexts))
                RenderTaskEntry(renderer, family, served, technologies, group, target, ScriptsFor, runner,
                    repositoryRoot, declared);
        }
    }

    private static string NoRootReason(GateCatalogueFamily family)
    {
        return $"no {family.Markers[0]} found; point this at your project and uncomment";
    }

    private static void RenderCouncilEntry(
        Renderer renderer,
        GateCatalogueFamily family,
        List<string> served,
        string technologies,
        IGrouping<string, FamilyGate> group,
        RootContext context,
        Func<string, HashSet<string>> scriptsFor,
        string runner,
        string repositoryRoot)
    {
        // Without a root every gate stays commented with the marker the host should point at;
        // conditions are not evaluated because there is no project to evaluate them against.
        if (context.IsPlaceholder)
        {
            string placeholderReason = NoRootReason(family);
            foreach (FamilyGate candidate in group)
                EmitCommentedCouncil(renderer, family, technologies, candidate, context, placeholderReason);

            return;
        }

        FamilyGate? selected = group.FirstOrDefault(candidate =>
            ConditionHolds(candidate, context, scriptsFor, runner, repositoryRoot));

        if (selected is null)
        {
            // No form holds: comment every variant out with its own reason rather than
            // silently dropping the gate, so the host sees what to enable.
            foreach (FamilyGate candidate in group)
            {
                EmitCommentedCouncil(
                    renderer, family, technologies, candidate, context,
                    ReasonFor(candidate, context));
            }

            return;
        }

        foreach (RootContext target in new[] { context })
            EmitLiveCouncil(renderer, family, served, technologies, selected, target, runner);
    }

    /// <summary>
    /// The contexts a gate group renders for. A gate that names no root is written once even
    /// when the family has several roots; every catalogue gate today names its root, so this
    /// only guards the shape rather than changing any output.
    /// </summary>
    private static IReadOnlyList<RootContext> GroupTargets(
        IGrouping<string, FamilyGate> group,
        IReadOnlyList<RootContext> contexts)
    {
        if (contexts.Count > 1 && group.All(candidate => !IsRootBound(candidate)))
            return [contexts[0] with { Suffix = string.Empty }];

        return contexts;
    }

    private static void EmitLiveCouncil(
        Renderer renderer,
        GateCatalogueFamily family,
        List<string> served,
        string technologies,
        FamilyGate selected,
        RootContext context,
        string runner)
    {
        string id = MakeUnique(renderer.CouncilIds, family.Family + "-" + selected.Id + context.Suffix);
        List<string> run = selected.Run.Select(argument => Substitute(argument, context)).ToList();
        List<string> include = selected.Include.Select(pattern => Substitute(pattern, context)).ToList();
        string? workingDirectory = WorkingDirectoryFor(selected, context);

        renderer.CouncilLines.Add($"    # {technologies}: {selected.Role} ({context.Label})");
        AddNoteComment(renderer.CouncilLines, selected, "    ");
        renderer.CouncilLines.AddRange(CouncilGateLines(id, run, include, selected.Os, workingDirectory,
            selected.Blocking, selected.TimeoutSeconds, "    ", selected.FailOnOutput));
        renderer.Written.Add((served[0], id));

        if (string.Equals(family.Family, "csharp", StringComparison.Ordinal) &&
            string.Equals(selected.Id, "test", StringComparison.Ordinal) &&
            string.Equals(runner, RunnerVstest, StringComparison.Ordinal))
        {
            renderer.CsharpTestVstestWritten = true;
        }

        if (string.Equals(family.Family, "python", StringComparison.Ordinal) &&
            string.Equals(selected.Id, "test", StringComparison.Ordinal))
        {
            renderer.PythonTestWritten = true;
        }
    }

    private static void RenderTaskEntry(
        Renderer renderer,
        GateCatalogueFamily family,
        List<string> served,
        string technologies,
        IGrouping<string, FamilyGate> group,
        RootContext context,
        Func<string, HashSet<string>> scriptsFor,
        string runner,
        string repositoryRoot,
        HashSet<string> declared)
    {
        // Without a root every gate stays commented with the marker the host should point at.
        if (context.IsPlaceholder)
        {
            string placeholderReason = NoRootReason(family);
            foreach (FamilyGate candidate in group)
            {
                foreach (string agent in TargetAgents(family, candidate, context, declared))
                    EmitCommentedTask(renderer, family, technologies, candidate, context, agent, placeholderReason);
            }

            return;
        }

        FamilyGate? selected = group.FirstOrDefault(candidate =>
            ConditionHolds(candidate, context, scriptsFor, runner, repositoryRoot));

        if (selected is null)
        {
            foreach (FamilyGate candidate in group)
            {
                string reason = ReasonFor(candidate, context);
                foreach (string agent in TargetAgents(family, candidate, context, declared))
                    EmitCommentedTask(renderer, family, technologies, candidate, context, agent, reason);
            }

            return;
        }

        foreach (string agent in TargetAgents(family, selected, context, declared))
            EmitLiveTask(renderer, family, served, technologies, selected, context, agent);
    }

    private static List<string> TargetAgents(
        GateCatalogueFamily family,
        FamilyGate entry,
        RootContext context,
        HashSet<string> declared)
    {
        // The rust family routes per root: a root under src-tauri belongs to tauri-dev when
        // tauri is declared, and to rust-dev otherwise. Every other root belongs to rust-dev.
        if (string.Equals(family.Family, "rust", StringComparison.Ordinal))
        {
            string target = HasSrcTauriSegment(context.RootDirectory) && declared.Contains("tauri")
                ? "tauri-dev"
                : "rust-dev";
            if (entry.Agents.Contains(target, StringComparer.Ordinal) &&
                AgentTechnology.TryGetValue(target, out string? technology) &&
                declared.Contains(technology))
            {
                return [target];
            }

            return [];
        }

        // An agent is written only for a declared technology: typescript-dev needs typescript,
        // react-dev needs react, and so on. test-dev is handled in its own pass below.
        List<string> agents = [];
        foreach (string agent in AgentOrder)
        {
            if (string.Equals(agent, "test-dev", StringComparison.Ordinal))
                continue;

            if (entry.Agents.Contains(agent, StringComparer.Ordinal) &&
                AgentTechnology.TryGetValue(agent, out string? technology) &&
                declared.Contains(technology))
            {
                agents.Add(agent);
            }
        }

        return agents;
    }

    private static void EmitLiveTask(
        Renderer renderer,
        GateCatalogueFamily family,
        List<string> served,
        string technologies,
        FamilyGate entry,
        RootContext context,
        string agent)
    {
        if (!renderer.AgentIds.TryGetValue(agent, out HashSet<string>? used))
        {
            used = new HashSet<string>(StringComparer.Ordinal);
            renderer.AgentIds[agent] = used;
        }

        string id = MakeUnique(used, family.Family + "-" + entry.Id + context.Suffix);
        SubstitutedTaskGate gate = SubstituteTaskGate(entry, id, context);

        if (!renderer.AgentLines.TryGetValue(agent, out List<string>? lines))
        {
            lines = [];
            renderer.AgentLines[agent] = lines;
        }

        lines.Add($"      # {technologies}: {entry.Role} ({context.Label})");
        AddNoteComment(lines, entry, "      ");
        lines.AddRange(TaskGateLines(gate, "      "));
        renderer.AgentsWithLive.Add(agent);
        renderer.Written.Add((AgentTechnology[agent], id));

        if (entry.Role is "format" or "lint")
            renderer.TaskRenders.Add(new LiveTaskRender(served[0], entry, gate));
    }

    private static void EmitCommentedTask(
        Renderer renderer,
        GateCatalogueFamily family,
        string technologies,
        FamilyGate entry,
        RootContext context,
        string agent,
        string reason)
    {
        string fullId = family.Family + "-" + entry.Id + context.Suffix;

        if (!renderer.AgentLines.TryGetValue(agent, out List<string>? lines))
        {
            lines = [];
            renderer.AgentLines[agent] = lines;
        }

        lines.Add($"      # {technologies}: {entry.Role} ({context.Label})");
        lines.Add($"      # {reason}");
        foreach (string line in TaskGateLines(SubstituteTaskGate(entry, fullId, context), "      "))
            lines.Add($"      # {line.TrimStart()}");

        AddCommentedOut(renderer, fullId, reason);
    }

    private static void EmitCommentedCouncil(
        Renderer renderer,
        GateCatalogueFamily family,
        string technologies,
        FamilyGate entry,
        RootContext context,
        string reason)
    {
        string id = family.Family + "-" + entry.Id + context.Suffix;
        List<string> run = entry.Run.Select(argument => Substitute(argument, context)).ToList();
        List<string> include = entry.Include.Select(pattern => Substitute(pattern, context)).ToList();

        renderer.CouncilLines.Add($"    # {technologies}: {entry.Role} ({context.Label})");
        renderer.CouncilLines.Add($"    # {reason}");
        foreach (string line in CouncilGateLines(id, run, include, entry.Os, WorkingDirectoryFor(entry, context),
                     entry.Blocking, entry.TimeoutSeconds, "    ", entry.FailOnOutput))
            renderer.CouncilLines.Add($"    # {line.TrimStart()}");

        AddCommentedOut(renderer, id, reason);
    }

    private static void RenderCapped(
        Renderer renderer,
        GateCatalogueFamily family,
        string technologies,
        ProjectRootSet set)
    {
        int cap = ProjectRootDetector.MaxRootsPerFamily;
        string reason =
            $"family has {set.Roots.Count} roots, past the cap of {cap}; pick the roots to gate and uncomment";
        RootContext context = new(".", family.Markers[0], string.Empty, "all roots", true);

        renderer.CouncilLines.Add($"    # {technologies}: every gate is commented out: {reason}.");
        renderer.CouncilLines.Add("    # pick the roots to gate, point each gate at one, and uncomment:");
        foreach (ProjectRoot root in set.Roots)
            renderer.CouncilLines.Add($"    # - {root.RootDirectory}");

        foreach (FamilyGate entry in family.Gates)
            EmitCommentedCouncil(renderer, family, technologies, entry, context, reason);

        foreach (FamilyGate entry in family.TaskGates)
        {
            foreach (string agent in entry.Agents)
            {
                if (!AgentOrder.Contains(agent, StringComparer.Ordinal) ||
                    string.Equals(agent, "test-dev", StringComparison.Ordinal))
                {
                    continue;
                }

                EmitCommentedTask(renderer, family, technologies, entry, context, agent, reason);
            }
        }
    }

    private static void RenderTestDev(Renderer renderer)
    {
        // test-dev receives a copy of every rendered task gate whose role is format or lint,
        // with no duplicate id. The copies follow the families that supplied them.
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (LiveTaskRender render in renderer.TaskRenders)
        {
            if (!seen.Add(render.Gate.Id))
                continue;

            const string agent = "test-dev";
            if (!renderer.AgentIds.TryGetValue(agent, out HashSet<string>? used))
            {
                used = new HashSet<string>(StringComparer.Ordinal);
                renderer.AgentIds[agent] = used;
            }

            string id = MakeUnique(used, render.Gate.Id);
            SubstitutedTaskGate gate = render.Gate with { Id = id };

            if (!renderer.AgentLines.TryGetValue(agent, out List<string>? lines))
            {
                lines = [];
                renderer.AgentLines[agent] = lines;
            }

            lines.Add($"      # {render.Technology}: {render.Entry.Role} (test-dev copy)");
            AddNoteComment(lines, render.Entry, "      ");
            lines.AddRange(TaskGateLines(gate, "      "));
            renderer.AgentsWithLive.Add(agent);
            renderer.Written.Add((render.Technology, id));
        }
    }

    private static string BuildYaml(Renderer renderer)
    {
        StringBuilder yaml = new();
        yaml.Append("# Gates written by Kyber-Weave ").Append(CatalogueVersion())
            .Append(" 'docs init' from its shipped catalogue.\n");
        yaml.Append(
            "# This block is yours: edit, remove or add gates freely; re-running 'docs init' never changes it.\n");
        yaml.Append("# 'kyber-weave review catalogue' prints the catalogue for newer releases, to copy in by hand.\n");
        yaml.Append("# CI: copy templates/github-actions/kyber-weave-review-gate.yml from\n");
        yaml.Append("# https://github.com/dpalfery/kyber-weave into .github/workflows/.\n");
        if (renderer.CsharpTestVstestWritten)
            yaml.Append(
                "# csharp (VSTest): 'dotnet test' exits 0 when no tests are found; set TreatNoTestsAsError to fail.\n");
        if (renderer.PythonTestWritten)
            yaml.Append("# python: pytest exit 5 (no tests collected) fails the gate on purpose.\n");

        yaml.Append("review:\n");
        yaml.Append("  gates:");
        if (renderer.CouncilLines.Count == 0)
        {
            yaml.Append(" []\n");
        }
        else
        {
            yaml.Append('\n');
            foreach (string line in renderer.CouncilLines)
                yaml.Append(line).Append('\n');
        }

        yaml.Append("  task-gates:");
        List<string> agents = AgentOrder.Where(renderer.AgentLines.ContainsKey).ToList();
        if (agents.Count == 0)
        {
            yaml.Append(" {}\n");
        }
        else
        {
            yaml.Append('\n');
            foreach (string agent in agents)
            {
                yaml.Append("    ").Append(renderer.AgentsWithLive.Contains(agent) ? agent + ":" : "# " + agent + ":")
                    .Append('\n');
                foreach (string line in renderer.AgentLines[agent])
                    yaml.Append(line).Append('\n');
            }
        }

        return yaml.ToString();
    }

    private static List<string> CouncilGateLines(
        string id,
        IReadOnlyList<string> run,
        IReadOnlyList<string> include,
        string? os,
        string? workingDirectory,
        bool blocking,
        int? timeoutSeconds,
        string indent,
        bool failOnOutput = false)
    {
        List<string> lines = [$"{indent}- id: {id}", $"{indent}  run: [{string.Join(", ", run.Select(Quote))}]"];
        if (!blocking)
            lines.Add($"{indent}  blocking: false");
        if (timeoutSeconds.HasValue)
            lines.Add($"{indent}  timeout-seconds: {timeoutSeconds.Value}");
        if (include.Count > 0)
            lines.Add($"{indent}  include: [{string.Join(", ", include.Select(Quote))}]");
        if (os is not null)
            lines.Add($"{indent}  os: {Quote(os)}");
        if (workingDirectory is not null)
            lines.Add($"{indent}  working-directory: {Quote(workingDirectory)}");
        if (failOnOutput)
            lines.Add($"{indent}  fail-on-output: true");

        return lines;
    }

    private static List<string> TaskGateLines(SubstitutedTaskGate gate, string indent)
    {
        List<string> lines = [$"{indent}- id: {gate.Id}"];
        if (gate.Include.Count > 0)
            lines.Add($"{indent}  include: [{string.Join(", ", gate.Include.Select(Quote))}]");
        if (gate.Os is not null)
            lines.Add($"{indent}  os: {Quote(gate.Os)}");
        lines.Add($"{indent}  run: [{string.Join(", ", gate.Run.Select(Quote))}]");
        if (gate.Fix.Count > 0)
            lines.Add($"{indent}  fix: [{string.Join(", ", gate.Fix.Select(Quote))}]");
        if (!gate.Blocking)
            lines.Add($"{indent}  blocking: false");
        if (gate.TimeoutSeconds.HasValue)
            lines.Add($"{indent}  timeout: {gate.TimeoutSeconds.Value}");
        if (gate.WorkingDirectory is not null)
            lines.Add($"{indent}  working-directory: {Quote(gate.WorkingDirectory)}");
        if (gate.FailOnOutput)
            lines.Add($"{indent}  fail-on-output: true");

        return lines;
    }

    /// <summary>
    /// A live gate's note is written as a comment under its label, so a host repository sees a
    /// requirement such as a minimum toolchain version beside the gate it affects.
    /// </summary>
    private static void AddNoteComment(List<string> lines, FamilyGate entry, string indent)
    {
        if (!string.IsNullOrWhiteSpace(entry.Note))
            lines.Add($"{indent}# {entry.Note.Trim()}");
    }

    private static SubstitutedTaskGate SubstituteTaskGate(FamilyGate entry, string id, RootContext context)
    {
        return new SubstitutedTaskGate(
            id,
            entry.Include.Select(pattern => Substitute(pattern, context)).ToList(),
            entry.Os,
            entry.Run.Select(argument => Substitute(argument, context)).ToList(),
            entry.Fix.Select(argument => Substitute(argument, context)).ToList(),
            entry.Blocking,
            entry.TimeoutSeconds,
            WorkingDirectoryFor(entry, context),
            entry.FailOnOutput);
    }

    private static string? WorkingDirectoryFor(FamilyGate entry, RootContext context)
    {
        if (entry.WorkingDirectory is null)
            return null;

        // The repository root is what an absent key already means, so it stays absent.
        string substituted = Substitute(entry.WorkingDirectory, context);
        return substituted is "." or "./" ? null : substituted;
    }

    private static bool ConditionHolds(
        FamilyGate entry,
        RootContext context,
        Func<string, HashSet<string>> scriptsFor,
        string runner,
        string repositoryRoot)
    {
        if (entry.When is null)
            return true;

        return entry.When.Kind switch
        {
            FamilyGateConditionKind.Optional => false,
            FamilyGateConditionKind.Runner =>
                string.Equals(entry.When.Argument, runner, StringComparison.Ordinal),
            FamilyGateConditionKind.Script => scriptsFor(context.RootDirectory)
                .Contains(entry.When.Argument ?? string.Empty),
            FamilyGateConditionKind.ScriptAbsent => !scriptsFor(context.RootDirectory)
                .Contains(entry.When.Argument ?? string.Empty),
            FamilyGateConditionKind.File => context.IsPlaceholder
                ? File.Exists(Path.Combine(repositoryRoot, entry.When.Argument ?? string.Empty))
                : File.Exists(Path.Combine(repositoryRoot,
                    context.RootDirectory == "." ? string.Empty : context.RootDirectory,
                    entry.When.Argument ?? string.Empty)),
            _ => true
        };
    }

    private static string ReasonFor(FamilyGate entry, RootContext context)
    {
        return entry.When?.Kind switch
        {
            FamilyGateConditionKind.Optional => entry.Note ?? "optional gate; uncomment to enable",
            FamilyGateConditionKind.Script =>
                $"package.json has no '{entry.When.Argument}' script; add it and uncomment",
            FamilyGateConditionKind.ScriptAbsent =>
                $"package.json defines a '{entry.When.Argument}' script, so this fallback is off; remove the script and uncomment",
            FamilyGateConditionKind.Runner => $"requires the '{entry.When.Argument}' test runner",
            FamilyGateConditionKind.File =>
                $"file '{entry.When.Argument}' not found under '{context.RootDirectory}'; create it and uncomment",
            _ => $"no {entry.When?.Argument} found; point this at your project and uncomment"
        };
    }

    private static bool IsRootBound(FamilyGate entry)
    {
        return ContainsPlaceholder(entry.Run)
               || ContainsPlaceholder(entry.Fix)
               || ContainsPlaceholder(entry.Include)
               || (entry.WorkingDirectory is not null &&
                   entry.WorkingDirectory.Contains("{{root", StringComparison.Ordinal));
    }

    private static bool ContainsPlaceholder(IReadOnlyList<string> values)
    {
        return values.Any(value =>
            value.Contains("{{root", StringComparison.Ordinal) || value.Contains("{{marker", StringComparison.Ordinal));
    }

    /// <summary>
    /// Replaces the render-time placeholders. <c>{{root-glob}}</c> goes first so the shared
    /// <c>{{root</c> prefix cannot shadow it, and the runtime tokens <c>{changed}</c> and
    /// <c>{task}</c> stay literal because they use single braces.
    /// </summary>
    private static string Substitute(string value, RootContext context)
    {
        return value
            .Replace("{{root-glob}}", context.RootDirectory == "." ? string.Empty : context.RootDirectory + "/",
                StringComparison.Ordinal)
            .Replace("{{root}}", context.RootDirectory, StringComparison.Ordinal)
            .Replace("{{marker}}", context.MarkerPath, StringComparison.Ordinal);
    }

    /// <summary>Reserves an id, appending <c>-2</c>, <c>-3</c> and so on in ordinal order on collision.</summary>
    private static string MakeUnique(HashSet<string> used, string candidate)
    {
        string id = candidate;
        for (int suffix = 2; !used.Add(id); suffix++)
            id = $"{candidate}-{suffix}";

        return id;
    }

    private static void AddCommentedOut(Renderer renderer, string id, string reason)
    {
        foreach ((string existingId, string existingReason) in renderer.CommentedOut)
        {
            if (string.Equals(existingId, id, StringComparison.Ordinal) &&
                string.Equals(existingReason, reason, StringComparison.Ordinal))
            {
                return;
            }
        }

        renderer.CommentedOut.Add((id, reason));
    }

    private static bool HasSrcTauriSegment(string rootDirectory)
    {
        if (rootDirectory == ".")
            return false;

        foreach (string segment in rootDirectory.Split('/'))
        {
            if (segment == "src-tauri")
                return true;
        }

        return false;
    }

    private static string DetectRunner(string repositoryRoot)
    {
        try
        {
            string path = Path.Combine(repositoryRoot, "global.json");
            if (!File.Exists(path))
                return RunnerVstest;

            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("test", out JsonElement test) &&
                test.ValueKind == JsonValueKind.Object &&
                test.TryGetProperty("runner", out JsonElement name) &&
                name.ValueKind == JsonValueKind.String &&
                string.Equals(name.GetString(), MtpRunnerName, StringComparison.Ordinal))
            {
                return RunnerMtp;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or unreadable global.json means the repository never opted into MTP.
        }

        return RunnerVstest;
    }

    private static HashSet<string> ReadPackageScripts(string repositoryRoot, string rootDirectory)
    {
        HashSet<string> scripts = new(StringComparer.Ordinal);
        try
        {
            string directory = rootDirectory == "."
                ? repositoryRoot
                : Path.Combine(repositoryRoot, rootDirectory);
            string path = Path.Combine(directory, "package.json");
            if (!File.Exists(path))
                return scripts;

            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("scripts", out JsonElement node) &&
                node.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in node.EnumerateObject())
                    scripts.Add(property.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Without a readable package.json no script condition holds; script-absent ones do.
            scripts.Clear();
        }

        return scripts;
    }

    private static string CatalogueVersion()
    {
        Assembly assembly = typeof(GateCatalogue).Assembly;
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(version))
            return "0.1.0";

        // A header comment cannot span lines, so only the first line ever reaches the block.
        int newline = version.IndexOfAny(['\r', '\n']);
        string first = (newline < 0 ? version : version[..newline]).Trim();
        return first.Length == 0 ? "0.1.0" : first;
    }

    /// <summary>Quotes a single-line string as a YAML scalar without changing its value.</summary>
    private static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl))
            throw new ArgumentException("YAML scalar values may not contain control characters.", nameof(value));

        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static List<FamilyGate> ParseGateList(YamlMappingNode root, string family, string key, bool taskGates)
    {
        YamlNode? node = ChildOf(root, key);
        if (node is null)
            return [];

        if (node is not YamlSequenceNode sequence)
            throw new InvalidDataException($"Gate catalogue family '{family}' {key} must be a list.");

        List<FamilyGate> gates = new(sequence.Children.Count);
        foreach (YamlNode child in sequence.Children)
            gates.Add(ParseGate(child, family, key, taskGates));

        return gates;
    }

    private static FamilyGate ParseGate(YamlNode node, string family, string list, bool taskGates)
    {
        string where = $"Gate catalogue family '{family}' {list}";
        if (node is not YamlMappingNode mapping)
            throw new InvalidDataException($"{where} has an entry that is not a mapping.");

        string id = RequiredScalar(mapping, where, "id");
        if (!ConfigSlug.IsValid(id))
            throw new InvalidDataException($"{where} gate id '{id}' is not a valid lookup name.");

        string role = RequiredScalar(mapping, where, "role");
        if (!KnownRoles.Contains(role, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{where} gate '{id}' role '{role}' is not recognised. Use 'format', 'lint', 'typecheck', 'build' or 'test'.");
        }

        List<string> run = RequiredStrings(mapping, "run", $"{where} gate '{id}'");
        List<string> fix = OptionalStrings(mapping, "fix") ?? [];
        List<string> include = OptionalStrings(mapping, "include") ?? [];
        string? os = OptionalScalar(mapping, "os");
        if (os is not null && os is not ("windows" or "unix"))
            throw new InvalidDataException(
                $"{where} gate '{id}' os '{os}' is not recognised. Use 'windows' or 'unix'.");

        string? workingDirectory = OptionalScalar(mapping, "working-directory");
        if (string.IsNullOrWhiteSpace(workingDirectory))
            workingDirectory = null;

        bool blocking = OptionalBoolean(mapping, "blocking", where, true);
        bool failOnOutput = OptionalBoolean(mapping, "fail-on-output", where, false);
        int? timeoutSeconds = OptionalTimeout(mapping, where, id);

        List<string> agents = OptionalStrings(mapping, "agents") ?? [];
        if (taskGates)
        {
            if (agents.Count == 0)
                throw new InvalidDataException(
                    $"{where} gate '{id}' names no agents. Every catalogue task gate supplies at least one developer agent.");

            foreach (string agent in agents)
            {
                if (!AgentOrder.Contains(agent, StringComparer.Ordinal))
                    throw new InvalidDataException(
                        $"{where} gate '{id}' agent '{agent}' is not a known developer agent slug.");
            }
        }
        else if (agents.Count > 0)
        {
            throw new InvalidDataException(
                $"{where} gate '{id}' names agents, but only task-gates entries supply agents.");
        }

        FamilyGateCondition? when = OptionalCondition(mapping, where, id);
        string? note = OptionalScalar(mapping, "note");

        return new FamilyGate(id, role, run, fix, include, os, workingDirectory, blocking, timeoutSeconds, agents, when,
            note, failOnOutput);
    }

    private static FamilyGateCondition? OptionalCondition(YamlMappingNode mapping, string where, string id)
    {
        YamlNode? node = ChildOf(mapping, "when");
        if (node is null)
            return null;

        if (node is YamlScalarNode scalar)
        {
            if (string.Equals(ScalarText(scalar), "optional", StringComparison.Ordinal))
                return new FamilyGateCondition(FamilyGateConditionKind.Optional, null);

            throw new InvalidDataException($"{where} gate '{id}' has a 'when' scalar that is not 'optional'.");
        }

        if (node is not YamlMappingNode condition || condition.Children.Count != 1)
            throw new InvalidDataException($"{where} gate '{id}' 'when' must name exactly one condition.");

        KeyValuePair<YamlNode, YamlNode> pair = condition.Children.First();
        string kind = ScalarText(pair.Key);
        string argument = ScalarText(pair.Value);

        if (kind == "file" && !IsRelativeUnderRoot(argument))
            throw new InvalidDataException(
                $"{where} gate '{id}' 'when: file' path '{argument}' must be relative and stay under the project root.");

        return kind switch
        {
            "file" when argument.Length > 0 => new FamilyGateCondition(FamilyGateConditionKind.File, argument),
            "script" when argument.Length > 0 => new FamilyGateCondition(FamilyGateConditionKind.Script, argument),
            "script-absent" when argument.Length > 0 => new FamilyGateCondition(FamilyGateConditionKind.ScriptAbsent,
                argument),
            "runner" when argument is "vstest" or "mtp" => new FamilyGateCondition(FamilyGateConditionKind.Runner,
                argument),
            "optional" when argument is "true" or "True" => new FamilyGateCondition(FamilyGateConditionKind.Optional,
                null),
            "optional" when argument is "false" or "False" => null,
            _ => throw new InvalidDataException($"{where} gate '{id}' 'when' condition '{kind}' is not recognised.")
        };
    }

    private static bool IsRelativeUnderRoot(string path)
    {
        if (path.Length == 0)
            return true;

        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':', StringComparison.Ordinal))
            return false;

        return !path.Split('/', '\\').Contains("..", StringComparer.Ordinal);
    }

    private static int? OptionalTimeout(YamlMappingNode mapping, string where, string id)
    {
        string? seconds = OptionalScalar(mapping, "timeout-seconds");
        string? shortForm = OptionalScalar(mapping, "timeout");
        if (seconds is null && shortForm is null)
            return null;

        if (seconds is not null && shortForm is not null &&
            !string.Equals(seconds, shortForm, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{where} gate '{id}' sets both 'timeout' and 'timeout-seconds' to different values. Keep one.");
        }

        string text = seconds ?? shortForm!;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value <= 0)
        {
            throw new InvalidDataException(
                $"{where} gate '{id}' timeout '{text}' must be greater than zero seconds.");
        }

        return value;
    }

    private static string RequiredScalar(YamlMappingNode mapping, string where, string key)
    {
        string? value = OptionalScalar(mapping, key);
        if (string.IsNullOrEmpty(value))
            throw new InvalidDataException($"{where} has an entry with no '{key}'. Give every entry a '{key}'.");

        return value;
    }

    private static string? OptionalScalar(YamlMappingNode mapping, string key)
    {
        YamlNode? node = ChildOf(mapping, key);
        if (node is null)
            return null;

        return ScalarText(node);
    }

    private static YamlMappingNode RequiredMapping(YamlMappingNode mapping, string key)
    {
        if (ChildOf(mapping, key) is not YamlMappingNode child)
            throw new InvalidDataException(
                $"Gate catalogue family has no '{key}' mapping. Declare '{key}' with its markers.");

        return child;
    }

    private static List<string> RequiredStrings(YamlMappingNode mapping, string key)
    {
        return RequiredStrings(mapping, key, "Gate catalogue family");
    }

    private static List<string> RequiredStrings(YamlMappingNode mapping, string key, string where)
    {
        List<string>? values = OptionalStrings(mapping, key);
        if (values is null || values.Count == 0)
            throw new InvalidDataException($"{where} has no '{key}'. Declare at least one '{key}'.");

        return values;
    }

    private static List<string>? OptionalStrings(YamlMappingNode mapping, string key)
    {
        YamlNode? node = ChildOf(mapping, key);
        if (node is null)
            return null;

        if (node is not YamlSequenceNode sequence)
            throw new InvalidDataException($"Gate catalogue '{key}' must be a list.");

        List<string> values = new(sequence.Children.Count);
        foreach (YamlNode child in sequence.Children)
        {
            string value = ScalarText(child);
            if (value.Length == 0)
                throw new InvalidDataException(
                    $"Gate catalogue '{key}' has an empty entry. Every entry must name something.");

            values.Add(value);
        }

        return values;
    }

    private static bool OptionalBoolean(YamlMappingNode mapping, string key, string where, bool defaultValue = false)
    {
        string? text = OptionalScalar(mapping, key);
        if (text is null)
            return defaultValue;

        if (bool.TryParse(text, out bool value))
            return value;

        throw new InvalidDataException($"{where} '{key}' value '{text}' is not a boolean. Use 'true' or 'false'.");
    }

    private static YamlNode? ChildOf(YamlMappingNode mapping, string name)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
        {
            if (string.Equals(ScalarText(pair.Key), name, StringComparison.Ordinal))
                return pair.Value;
        }

        return null;
    }

    private static string ScalarText(YamlNode? node)
    {
        return node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : string.Empty;
    }

    /// <summary>One root a gate is written for, with the id suffix and comment label its copies carry.</summary>
    /// <param name="RootDirectory">Repository-relative, forward-slashed. <c>.</c> is the repository root.</param>
    /// <param name="MarkerPath">Repository-relative path of the marker that made this directory a root.</param>
    /// <param name="Suffix">Appended to the gate id; empty for a single root.</param>
    /// <param name="Label">The root as named in the per-gate comment.</param>
    /// <param name="IsPlaceholder">True when no root exists and placeholder values stand in.</param>
    private sealed record RootContext(
        string RootDirectory,
        string MarkerPath,
        string Suffix,
        string Label,
        bool IsPlaceholder);

    private sealed record SubstitutedTaskGate(
        string Id,
        IReadOnlyList<string> Include,
        string? Os,
        IReadOnlyList<string> Run,
        IReadOnlyList<string> Fix,
        bool Blocking,
        int? TimeoutSeconds,
        string? WorkingDirectory,
        bool FailOnOutput = false);

    private sealed record LiveTaskRender(string Technology, FamilyGate Entry, SubstitutedTaskGate Gate);

    private sealed class Renderer
    {
        public readonly List<string> CouncilLines = [];
        public readonly Dictionary<string, List<string>> AgentLines = new(StringComparer.Ordinal);
        public readonly HashSet<string> AgentsWithLive = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<string>> AgentIds = new(StringComparer.Ordinal);
        public readonly HashSet<string> CouncilIds = new(StringComparer.Ordinal);
        public readonly List<(string Technology, string GateId)> Written = [];
        public readonly List<(string GateId, string Reason)> CommentedOut = [];
        public readonly List<LiveTaskRender> TaskRenders = [];
        public bool CsharpTestVstestWritten;
        public bool PythonTestWritten;
    }
}
