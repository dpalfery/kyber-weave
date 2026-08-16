using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Composes registered per-harness renderers into a single <see cref="ISquadRenderer"/>:
/// gates on target coverage before any renderer runs, dispatches each requested target to
/// the renderer that owns it, and validates the merged output against the safety
/// invariants every harness must uphold.
/// </summary>
/// <remarks>
/// The coverage gate and the validation pass both used to be dead code. Coverage was
/// implicit in whatever APM happened to support, discovered only by APM's own error text.
/// Validation lived in <c>SquadApmCompiler</c>, exercised solely by its own contract
/// tests — <see cref="SquadLifecycleService"/> called the render port directly and never
/// validated what came back. Both are wired into the real install/update path here.
/// </remarks>
public sealed class SquadRendererRegistry : ISquadRenderer
{
    private static readonly string[] SharedConductorIdentities = ["conductor", "conductor-v3"];

    private static readonly string[] DistinctBodyCollisions =
    [
        "dal-dev",
        "dotnet-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "python-dev",
        "test-dev"
    ];

    private readonly IReadOnlyDictionary<SquadTarget, ISquadRenderer> _byTarget;

    public SquadRendererRegistry(IEnumerable<ISquadRenderer> renderers)
    {
        ArgumentNullException.ThrowIfNull(renderers);

        Dictionary<SquadTarget, ISquadRenderer> byTarget = new();
        foreach (ISquadRenderer renderer in renderers)
        {
            foreach (SquadTarget target in renderer.SupportedTargets)
            {
                if (!byTarget.TryAdd(target, renderer))
                {
                    throw new ArgumentException(
                        $"Target '{SquadTargetCatalog.GetToken(target)}' is claimed by more than one renderer.",
                        nameof(renderers));
                }
            }
        }

        _byTarget = byTarget;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets => _byTarget.Keys.ToArray();

    /// <inheritdoc />
    public async Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<SquadTarget> unsupported = request.Targets
            .Where(target => !_byTarget.ContainsKey(target))
            .ToArray();
        if (unsupported.Count > 0)
        {
            string names = string.Join(", ", unsupported.Select(SquadTargetCatalog.GetToken).Order(StringComparer.Ordinal));
            string supported = string.Join(", ", SupportedTargets.Select(SquadTargetCatalog.GetToken).Order(StringComparer.Ordinal));
            return new SquadRenderResult(
                Success: false,
                Files: [],
                Degradations: [],
                Warnings: [],
                Errors:
                [
                    $"No renderer is implemented yet for target(s): {names}. " +
                    $"See docs/todo/<target>.md for what is needed to add support. " +
                    $"Targets available today: {supported}."
                ]);
        }

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];
        List<SquadRenderWarning> warnings = [];
        List<string> errors = [];

        foreach (IGrouping<ISquadRenderer, SquadTarget> group in request.Targets.GroupBy(target => _byTarget[target]))
        {
            SquadRenderResult partial = await group.Key.RenderAsync(
                request with { Targets = group.ToArray() },
                cancellationToken).ConfigureAwait(false);

            files.AddRange(partial.Files);
            degradations.AddRange(partial.Degradations);
            warnings.AddRange(partial.Warnings);
            errors.AddRange(partial.Errors);
        }

        if (errors.Count > 0)
        {
            return new SquadRenderResult(false, files, degradations, warnings, errors);
        }

        // The source is reloaded here, separately from whatever each renderer parsed
        // internally, because validation needs the canonical model (agent digests, the
        // full agent/skill roster) regardless of which renderers ran or how they read it.
        // The corpus is a few dozen small YAML/Markdown files, so the second parse costs
        // low single-digit milliseconds — trading that for a renderer contract that
        // doesn't leak a pre-parsed model through every implementation, including the
        // test fake that never touches disk at all.
        SquadSource source = SquadSourceLoader.Load(request.SourceDirectory);
        ValidateRenderResult(source, request.Targets, files, degradations);

        return new SquadRenderResult(true, files, degradations, warnings, []);
    }

    private static void ValidateRenderResult(
        SquadSource source,
        IReadOnlyList<SquadTarget> targets,
        IReadOnlyList<SquadDeploymentFile> files,
        IReadOnlyList<SquadDegradationRecord> degradations)
    {
        Dictionary<string, SquadAgent> agentMap = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);
        HashSet<string> targetTokens = targets.Select(SquadTargetCatalog.GetToken).ToHashSet(StringComparer.Ordinal);

        foreach (SquadDeploymentFile file in files)
        {
            try
            {
                string normalized = SquadPathPolicy.NormalizeRelativePath(file.RelativePath);
                if (!string.Equals(normalized, file.RelativePath, StringComparison.Ordinal))
                {
                    throw new SquadRenderValidationException(
                        $"Deployment file path '{file.RelativePath}' is not a canonical portable path.");
                }
            }
            catch (Exception ex) when (ex is not SquadRenderValidationException)
            {
                throw new SquadRenderValidationException(
                    $"Deployment file path '{file.RelativePath}' violates portable path constraints: {ex.Message}",
                    ex);
            }

            if (!targetTokens.Contains(file.Target))
            {
                throw new SquadRenderValidationException(
                    $"Deployment file '{file.RelativePath}' specifies target '{file.Target}' which was not in the requested target set.");
            }
        }

        foreach (SquadTarget target in targets)
        {
            string token = SquadTargetCatalog.GetToken(target);
            bool isNative = target is SquadTarget.Codex or SquadTarget.Cursor or SquadTarget.Claude or
                            SquadTarget.Copilot or SquadTarget.OpenCode or SquadTarget.Kilo or SquadTarget.Factory;

            List<SquadDeploymentFile> targetFiles = files
                .Where(f => string.Equals(f.Target, token, StringComparison.Ordinal))
                .ToList();

            if (isNative)
            {
                foreach (SquadDeploymentFile file in targetFiles)
                {
                    if (file.RelativePath.Contains("role-", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SquadRenderValidationException(
                            $"Native target '{token}' emitted role-prefixed file '{file.RelativePath}'.");
                    }
                }

                foreach (string conductor in SharedConductorIdentities)
                {
                    List<SquadDeploymentFile> conductorFiles = targetFiles.Where(f =>
                        f.RelativePath.EndsWith($"/{conductor}.toml", StringComparison.Ordinal) ||
                        f.RelativePath.EndsWith($"/{conductor}.md", StringComparison.Ordinal) ||
                        f.RelativePath.EndsWith($"/{conductor}.agent.md", StringComparison.Ordinal) ||
                        f.RelativePath.Contains($"/{conductor}/SKILL.md", StringComparison.Ordinal)).ToList();

                    if (conductorFiles.Count > 1)
                    {
                        throw new SquadRenderValidationException(
                            $"Duplicate projection detected for '{conductor}' on native target '{token}'.");
                    }

                    if (conductorFiles.Any(f => f.RelativePath.Contains("/skills/", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new SquadRenderValidationException(
                            $"Native target '{token}' emitted skill for primary agent '{conductor}', violating single-projection rule.");
                    }
                }
            }
            else
            {
                if (targetFiles.Any(f => f.RelativePath.Contains("/agents/", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new SquadRenderValidationException(
                        $"Fallback target '{token}' emitted native agent file, which is not supported.");
                }

                foreach (string conductor in SharedConductorIdentities)
                {
                    List<SquadDeploymentFile> conductorFiles = targetFiles.Where(f =>
                        f.RelativePath.Contains($"/{conductor}/SKILL.md", StringComparison.Ordinal)).ToList();

                    if (conductorFiles.Count > 1)
                    {
                        throw new SquadRenderValidationException(
                            $"Duplicate skill projection detected for '{conductor}' on fallback target '{token}'.");
                    }
                }

                foreach (string collision in DistinctBodyCollisions)
                {
                    if (source.Agents.Any(a => string.Equals(a.Name, collision, StringComparison.Ordinal)) &&
                        source.Skills.Any(s => string.Equals(s.Name, collision, StringComparison.Ordinal)))
                    {
                        bool hasCanonicalSkill = targetFiles.Any(f => f.RelativePath.Contains($"/{collision}/SKILL.md", StringComparison.Ordinal));
                        bool hasRoleSkill = targetFiles.Any(f => f.RelativePath.Contains($"/role-{collision}/SKILL.md", StringComparison.Ordinal));

                        if (!hasCanonicalSkill || !hasRoleSkill)
                        {
                            throw new SquadRenderValidationException(
                                $"Fallback target '{token}' must contain both canonical skill '{collision}' and lowered role skill 'role-{collision}'.");
                        }
                    }
                }
            }
        }

        foreach (SquadDegradationRecord degradation in degradations)
        {
            if (!agentMap.TryGetValue(degradation.CanonicalIdentity, out SquadAgent? agent))
            {
                throw new SquadRenderValidationException(
                    $"Degradation record references unknown agent '{degradation.CanonicalIdentity}'.");
            }

            if (string.IsNullOrWhiteSpace(degradation.InstructionDigest))
            {
                throw new SquadRenderValidationException(
                    $"Degradation record for agent '{degradation.CanonicalIdentity}' is missing the instruction body digest.");
            }

            if (!string.Equals(degradation.InstructionDigest, agent.BodyDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new SquadRenderValidationException(
                    $"Degradation record for agent '{degradation.CanonicalIdentity}' has mismatched instruction digest '{degradation.InstructionDigest}' (expected '{agent.BodyDigest}').");
            }

            if (string.Equals(degradation.Code, "widened", StringComparison.OrdinalIgnoreCase) ||
                (degradation.Details is not null && degradation.Details.Contains("widening", StringComparison.OrdinalIgnoreCase)))
            {
                throw new SquadRenderValidationException(
                    $"Permission widening detected for agent '{degradation.CanonicalIdentity}'.");
            }
        }
    }
}
