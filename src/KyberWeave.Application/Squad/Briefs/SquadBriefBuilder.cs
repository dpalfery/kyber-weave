using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Application.Arbiter;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>Resolves a brief's facts and reference directory, then assembles it.</summary>
/// <param name="briefs">Assembles the brief and finds each reference file.</param>
/// <param name="files">Reads the plan, the root AGENTS.md and probes the reference directories.</param>
/// <param name="plans">Probes the planning folders a plan argument is resolved in.</param>
/// <param name="globalRoots">Resolves each harness's global install root.</param>
/// <param name="squadSources">Finds the canonical Squad source of a checkout.</param>
/// <param name="environment">The directory a relative plan argument is read against.</param>
/// <param name="paths">Turns a plan or reference-directory argument into a full path.</param>
public sealed class SquadBriefBuilder(
    ISquadBriefService briefs,
    IFileSystem files,
    IRepositoryFileStore plans,
    ISquadGlobalRootResolver globalRoots,
    ISquadSourceLocator squadSources,
    IProcessEnvironment environment,
    IPathResolver paths) : ISquadBriefBuilder
{
    /// <inheritdoc />
    public string? ResolveAutonomy(string repositoryRoot, string? given, KyberWeaveConfig config)
    {
        if (!Accepts("autonomy", given, out string refusal))
        {
            throw new ArgumentException(refusal, nameof(given));
        }

        (string value, string source) = BriefAutonomy.Resolve(given, ReadRootAgents(repositoryRoot), config?.ConfigReg.Autonomy);
        return string.Equals(source, "default", StringComparison.Ordinal) ? null : value;
    }

    /// <inheritdoc />
    public bool TryBuild(SquadBriefRequest request, out BriefResult? result, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(request);

        result = null;
        Dictionary<string, string> sources = new(StringComparer.Ordinal);

        if (!Accepts("path", request.Path, out refusal) || !Accepts("autonomy", request.Autonomy, out refusal) || !Accepts("mode", request.Mode, out refusal))
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
            if (!TryReadPlan(request, out PlanDocument? plan, out refusal) || plan is null)
            {
                return false;
            }

            planStatus = plan.Status ?? "none";
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
        (string autonomy, string autonomySource) = BriefAutonomy.Resolve(
            request.Autonomy, agentsMarkdown, request.Config?.ConfigReg.Autonomy);
        sources["autonomy"] = request.Autonomy is null ? autonomySource : "given";

        BriefFacts facts = new(path, autonomy, mode, sources, planStatus);

        BriefMap map;
        try
        {
            map = BriefMapLoader.Load();
        }
        catch (BriefMapException exception)
        {
            refusal = exception.Message;
            return false;
        }

        if (map.FilesOf(request.Agent) is null)
        {
            refusal = $"No brief is defined for agent '{request.Agent}'. Agents with one: {string.Join(", ", map.Agents)}.";
            return false;
        }

        if (!TryFindReferences(request, map, out string? directory, out refusal) || directory is null)
        {
            return false;
        }

        try
        {
            result = briefs.Assemble(map, request.Agent, facts, directory);
            return true;
        }
        catch (BriefMapException exception)
        {
            refusal = exception.Message;
            return false;
        }
    }

    /// <inheritdoc />
    public bool AcceptsFacts(string? path, string? autonomy, out string refusal) =>
        Accepts("path", path, out refusal) && Accepts("autonomy", autonomy, out refusal);

    private static bool Accepts(string fact, string? value, out string error)
    {
        error = string.Empty;
        if (value is null)
        {
            return true;
        }

        // Autonomy is read the way every other path reads it: trimmed and case-insensitive.
        // Comparing the raw text here refused a value the resolver accepts.
        if (string.Equals(fact, "autonomy", StringComparison.Ordinal)
            ? BriefAutonomy.Normalize(value) is not null
            : BriefFacts.Vocabulary[fact].Contains(value, StringComparer.Ordinal))
        {
            return true;
        }

        error = $"'{value}' is not a value of {fact}. Use one of: {string.Join(", ", BriefFacts.Vocabulary[fact])}.";
        return false;
    }

    private bool TryReadPlan(SquadBriefRequest request, out PlanDocument? plan, out string error)
    {
        ArgumentNullException.ThrowIfNull(request);
        string planArgument = request.Plan ?? throw new ArgumentException("A plan argument was required but none was supplied.", nameof(request));

        plan = null;
        DiagnosticReport report = new();
        KyberWeaveConfig config = request.Config ?? KyberWeaveConfigDefaults.Product;
        if (!TryResolvePlan(request.RepositoryRoot, config, planArgument, report, out string relative))
        {
            error = string.Join(" ", report.Items.Select(item => item.Message));
            return false;
        }

        string full = paths.GetFullPath(relative, request.RepositoryRoot);
        if (!files.FileExists(full))
        {
            error = $"No plan file '{request.Plan}' was found ({full}). Pass its path, or its file name or slug under the plans folder.";
            return false;
        }

        plan = PlanDocumentParser.Parse(files.ReadAllText(full));
        error = string.Empty;
        return true;
    }

    private bool TryResolvePlan(string root, KyberWeaveConfig config, string argument, DiagnosticReport report, out string relative)
    {
        string current = paths.GetFullPath(environment.CurrentDirectory, environment.CurrentDirectory);
        PlanPathResolution resolution = new PlanFileResolver(root, config, plans, paths, environment).Resolve(argument, current);
        if (resolution.Found && resolution.Path is { } path)
        {
            relative = path;
            return true;
        }

        if (resolution.Ambiguous)
        {
            report.Add(new Diagnostic(
                PlanReferenceRuleIds.Ambiguous,
                Severity.Error,
                $"'{argument}' matches {resolution.Candidates.Count} plans: {string.Join(", ", resolution.Candidates)}.",
                "squad brief",
                Hint: "Name one of them: its full file name including the date prefix, or its path."));
            relative = argument;
            return false;
        }

        string full = paths.GetFullPath(argument, current);
        relative = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');
        return true;
    }

    private string? ReadRootAgents(string repositoryRoot)
    {
        string file = System.IO.Path.Combine(repositoryRoot, "AGENTS.md");
        try
        {
            return files.FileExists(file) ? files.ReadAllText(file) : null;
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return null;
        }
    }

    private bool TryFindReferences(SquadBriefRequest request, BriefMap map, out string? directory, out string error)
    {
        directory = null;
        error = string.Empty;

        if (map.FilesOf(request.Agent) is not { } rules)
        {
            error = $"No brief is defined for agent '{request.Agent}'.";
            return false;
        }

        string[] mapped = [.. rules.Select(rule => rule.File)];

        if (!string.IsNullOrWhiteSpace(request.From))
        {
            string given = paths.GetFullPath(request.From, request.RepositoryRoot);
            if (!files.DirectoryExists(given))
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
            string? source = squadSources.Resolve(root);
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
    private List<string> Installed(SquadBriefRequest request, SquadTarget? target)
    {
        try
        {
            return [.. SquadReferenceLocator.Candidates(request.Agent, target, request.ProjectRoots, globalRoots)];
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return [.. SquadReferenceLocator.Candidates(request.Agent, target, request.ProjectRoots, null)];
        }
    }

    private bool HoldsAny(string directory, string[] mappedFiles)
    {
        if (!files.DirectoryExists(directory))
        {
            return false;
        }

        return mappedFiles.Any(file =>
        {
            try
            {
                briefs.ResolveFile(directory, file);
                return true;
            }
            catch (BriefMapException)
            {
                return false;
            }
        });
    }
}
