using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Briefs;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Cli.Commands.Squad.Brief;

/// <summary>What a brief is built from. Facts left null are resolved by the machine where they can be.</summary>
/// <param name="Agent">The agent whose reference files are read.</param>
/// <param name="Path">The route, or null.</param>
/// <param name="Autonomy">The autonomy for this run, or null to read the repository's.</param>
/// <param name="Mode">The development mode, or null to read the plan's.</param>
/// <param name="Plan">A plan file in any spelling the plan resolver accepts, or null.</param>
/// <param name="Harness">The harness the references are installed for, or null to try every harness.</param>
/// <param name="From">A directory holding the reference files, or null to find the installed ones.</param>
/// <param name="RepositoryRoot">The repository root the configuration and plans are read from.</param>
/// <param name="ProjectRoots">The checkouts to look under for project-scope installs, most specific first.</param>
/// <param name="Config">The loaded configuration, or null when it did not load.</param>
internal sealed record SquadBriefRequest(
    string Agent,
    string? Path,
    string? Autonomy,
    string? Mode,
    string? Plan,
    string? Harness,
    string? From,
    string RepositoryRoot,
    IReadOnlyList<string> ProjectRoots,
    KyberWeaveConfig? Config);

/// <summary>Resolves a brief's facts and reference directory, then assembles it.</summary>
internal static class SquadBriefBuilder
{
    /// <summary>
    /// Builds the brief. A failure is a message, not an exception: a refused fact, an unknown agent, a plan
    /// that cannot be read, or reference files that cannot be found each say what to change.
    /// </summary>
    public static bool TryBuild(SquadBriefRequest request, out BriefResult? result, out string error)
    {
        result = null;
        Dictionary<string, string> sources = new(StringComparer.Ordinal);

        if (!Accepts("path", request.Path, out error) || !Accepts("autonomy", request.Autonomy, out error) || !Accepts("mode", request.Mode, out error))
        {
            return false;
        }

        string? path = request.Path;
        string? mode = request.Mode;
        string? planStatus = null;
        if (path is not null)
        {
            sources["path"] = "given";
        }

        if (mode is not null)
        {
            sources["mode"] = "given";
        }

        if (!string.IsNullOrWhiteSpace(request.Plan))
        {
            if (!TryReadPlan(request, out PlanDocument? plan, out error))
            {
                return false;
            }

            planStatus = plan!.Status ?? "none";
            if (mode is null)
            {
                mode = plan.DevelopmentMode;
                sources["mode"] = "plan frontmatter";
            }

            if (path is null)
            {
                path = "plan";
                sources["path"] = "--plan";
            }
        }

        string? agentsMarkdown = ReadRootAgents(request.RepositoryRoot);
        (string autonomy, string autonomySource) = BriefFacts.ResolveAutonomy(
            request.Autonomy, agentsMarkdown, request.Config?.ConfigReg.Autonomy);
        sources["autonomy"] = request.Autonomy is null ? autonomySource : "given";

        BriefFacts facts = new(path, autonomy, mode, sources, planStatus);

        BriefMap map;
        try
        {
            map = BriefMap.Load();
        }
        catch (BriefMapException exception)
        {
            error = exception.Message;
            return false;
        }

        if (map.FilesOf(request.Agent) is null)
        {
            error = $"No brief is defined for agent '{request.Agent}'. Agents with one: {string.Join(", ", map.Agents)}.";
            return false;
        }

        if (!TryFindReferences(request, map, out string? directory, out error))
        {
            return false;
        }

        try
        {
            result = BriefAssembler.Assemble(map, request.Agent, facts, directory!);
            return true;
        }
        catch (BriefMapException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Checks the facts a caller may give on the command line against the vocabulary.</summary>
    public static bool AcceptsFacts(string? path, string? autonomy, out string error) =>
        Accepts("path", path, out error) && Accepts("autonomy", autonomy, out error);

    private static bool Accepts(string fact, string? value, out string error)
    {
        error = string.Empty;
        if (value is null || BriefFacts.Vocabulary[fact].Contains(value, StringComparer.Ordinal))
        {
            return true;
        }

        error = $"'{value}' is not a value of {fact}. Use one of: {string.Join(", ", BriefFacts.Vocabulary[fact])}.";
        return false;
    }

    private static bool TryReadPlan(SquadBriefRequest request, out PlanDocument? plan, out string error)
    {
        plan = null;
        DiagnosticReport report = new();
        KyberWeaveConfig config = request.Config ?? KyberWeaveConfig.ProductDefaults;
        if (!PlanArguments.TryResolve(request.RepositoryRoot, config, request.Plan!, null, "squad brief", report, out string relative))
        {
            error = string.Join(" ", report.Items.Select(item => item.Message));
            return false;
        }

        string full = System.IO.Path.GetFullPath(relative, request.RepositoryRoot);
        if (!File.Exists(full))
        {
            error = $"No plan file '{request.Plan}' was found ({full}). Pass its path, or its file name or slug under the plans folder.";
            return false;
        }

        plan = PlanDocumentParser.Parse(File.ReadAllText(full));
        error = string.Empty;
        return true;
    }

    private static string? ReadRootAgents(string repositoryRoot)
    {
        string file = System.IO.Path.Combine(repositoryRoot, "AGENTS.md");
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryFindReferences(SquadBriefRequest request, BriefMap map, out string? directory, out string error)
    {
        directory = null;
        error = string.Empty;
        string[] mapped = [.. map.FilesOf(request.Agent)!.Select(rule => rule.File)];

        if (!string.IsNullOrWhiteSpace(request.From))
        {
            string given = System.IO.Path.GetFullPath(request.From, request.RepositoryRoot);
            if (!Directory.Exists(given))
            {
                error = $"The reference directory '{given}' does not exist.";
                return false;
            }

            directory = given;
            return true;
        }

        SquadTarget? target = request.Harness is not null && SquadTargetCatalog.TryResolve(request.Harness, out SquadTarget resolved)
            ? resolved
            : null;

        List<string> candidates = Installed(request, target);
        foreach (string root in request.ProjectRoots)
        {
            string? source = SquadPackSourceLocator.Resolve(root);
            if (source is not null)
            {
                candidates.Add(SquadReferenceLocator.InSource(source, request.Agent));
            }
        }

        directory = candidates.Distinct(StringComparer.Ordinal).FirstOrDefault(candidate => HoldsAny(candidate, mapped));
        if (directory is not null)
        {
            return true;
        }

        error = $"No installed reference files for agent '{request.Agent}' were found. Looked in: " +
            string.Join("; ", candidates.Distinct(StringComparer.Ordinal)) +
            ". Install the squad ('kyber-weave squad install'), or pass --from <directory>.";
        return false;
    }

    /// <summary>
    /// Project-scope candidates, then global ones. A global root that cannot be resolved, such as an override
    /// variable that is not an absolute path, drops global scope instead of failing the brief.
    /// </summary>
    private static List<string> Installed(SquadBriefRequest request, SquadTarget? target)
    {
        try
        {
            return [.. SquadReferenceLocator.Candidates(request.Agent, target, request.ProjectRoots, SquadCommandComposition.ResolveGlobalRoots())];
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return [.. SquadReferenceLocator.Candidates(request.Agent, target, request.ProjectRoots, null)];
        }
    }

    private static bool HoldsAny(string directory, string[] files)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        return files.Any(file =>
        {
            try
            {
                BriefAssembler.ResolveFile(directory, file);
                return true;
            }
            catch (BriefMapException)
            {
                return false;
            }
        });
    }
}
