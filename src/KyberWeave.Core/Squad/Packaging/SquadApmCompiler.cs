using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Packaging;

/// <summary>
/// Defines the contract for orchestrating APM compilation and validating structured output.
/// </summary>
public interface ISquadApmCompiler
{
    /// <summary>
    /// Compiles the specified Squad source for the given targets and scope using the configured APM runner.
    /// </summary>
    /// <param name="source">The validated Squad source.</param>
    /// <param name="targets">The explicit target roster to compile for.</param>
    /// <param name="scope">The deployment scope (project or global).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verified compilation result.</returns>
    Task<SquadApmCompilationResult> CompileAsync(
        SquadSource source,
        IReadOnlyList<SquadTarget> targets,
        SquadDeploymentScope scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates APM compilation by writing staging source, running the APM compiler,
/// validating the structured output against safety invariants, and returning verified deployment files.
/// </summary>
public sealed class SquadApmCompiler : ISquadApmCompiler
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

    private readonly IApmRunner _runner;
    private readonly ISquadApmSourceWriter _writer;

    /// <summary>
    /// Initializes a new instance of <see cref="SquadApmCompiler"/> with the specified runner and writer.
    /// </summary>
    /// <param name="runner">The APM runner port.</param>
    /// <param name="writer">The APM source writer collaborator.</param>
    public SquadApmCompiler(IApmRunner runner, ISquadApmSourceWriter writer)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(writer);
        _runner = runner;
        _writer = writer;
    }

    /// <summary>
    /// Compiles the specified Squad source for the given targets and scope using the injected APM runner.
    /// </summary>
    /// <param name="source">The validated Squad source.</param>
    /// <param name="targets">The explicit target roster to compile for.</param>
    /// <param name="scope">The deployment scope (project or global).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verified compilation result.</returns>
    public async Task<SquadApmCompilationResult> CompileAsync(
        SquadSource source,
        IReadOnlyList<SquadTarget> targets,
        SquadDeploymentScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
        {
            throw new ArgumentException("Targets list cannot be empty.", nameof(targets));
        }

        string tempStagingDir = Path.Combine(
            Path.GetTempPath(),
            "kyber-squad-apm-staging-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempStagingDir);
            _writer.Write(source, tempStagingDir);

            ApmRenderRequest request = new(
                SourceDirectory: tempStagingDir,
                Targets: targets,
                Scope: scope,
                TranslationMode: "best-effort");

            ApmRenderResult renderResult = await _runner.RenderAsync(request, cancellationToken).ConfigureAwait(false);

            if (!renderResult.Success || renderResult.Errors.Count > 0)
            {
                string errorMessage = renderResult.Errors.Count > 0
                    ? string.Join(Environment.NewLine, renderResult.Errors)
                    : "APM compilation failed with unspecified error.";
                throw new SquadApmValidationException(errorMessage);
            }

            ValidateRenderResult(source, targets, renderResult);

            List<SquadDegradation> degradations = renderResult.Degradations
                .Select(d => new SquadDegradation(d.Target, d.CanonicalIdentity, d.Code))
                .ToList();

            return new SquadApmCompilationResult(
                source,
                renderResult.Files,
                degradations,
                renderResult.Degradations);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempStagingDir))
                {
                    Directory.Delete(tempStagingDir, recursive: true);
                }
            }
            catch
            {
                // Staging cleanup failure should not prevent return of valid result
            }
        }
    }

    private static void ValidateRenderResult(
        SquadSource source,
        IReadOnlyList<SquadTarget> targets,
        ApmRenderResult result)
    {
        Dictionary<string, SquadAgent> agentMap = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);
        HashSet<string> targetTokens = targets.Select(SquadTargetCatalog.GetToken).ToHashSet(StringComparer.Ordinal);

        // 1. Validate all file paths are canonical portable relative paths and targets are valid
        foreach (SquadDeploymentFile file in result.Files)
        {
            try
            {
                string normalized = SquadPathPolicy.NormalizeRelativePath(file.RelativePath);
                if (!string.Equals(normalized, file.RelativePath, StringComparison.Ordinal))
                {
                    throw new SquadApmValidationException(
                        $"Deployment file path '{file.RelativePath}' is not a canonical portable path.");
                }
            }
            catch (Exception ex) when (ex is not SquadApmValidationException)
            {
                throw new SquadApmValidationException(
                    $"Deployment file path '{file.RelativePath}' violates portable path constraints: {ex.Message}",
                    ex);
            }

            if (!targetTokens.Contains(file.Target))
            {
                throw new SquadApmValidationException(
                    $"Deployment file '{file.RelativePath}' specifies target '{file.Target}' which was not in the requested target set.");
            }
        }

        // 2. Validate single projection rules and target-specific constraints
        foreach (SquadTarget target in targets)
        {
            string token = SquadTargetCatalog.GetToken(target);
            bool isNative = target is SquadTarget.Codex or SquadTarget.Cursor or SquadTarget.Claude or
                            SquadTarget.Copilot or SquadTarget.OpenCode or SquadTarget.Kilo or SquadTarget.Factory;

            List<SquadDeploymentFile> targetFiles = result.Files.Where(f => string.Equals(f.Target, token, StringComparison.Ordinal)).ToList();

            if (isNative)
            {
                // Native target must NOT emit role- prefixed files
                foreach (SquadDeploymentFile file in targetFiles)
                {
                    if (file.RelativePath.Contains("role-", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SquadApmValidationException(
                            $"Native target '{token}' emitted role-prefixed file '{file.RelativePath}'.");
                    }
                }

                // Conductor and conductor-v3 single projection rule on native target
                foreach (string conductor in SharedConductorIdentities)
                {
                    List<SquadDeploymentFile> conductorFiles = targetFiles.Where(f =>
                        f.RelativePath.EndsWith($"/{conductor}.toml", StringComparison.Ordinal) ||
                        f.RelativePath.EndsWith($"/{conductor}.md", StringComparison.Ordinal) ||
                        f.RelativePath.Contains($"/{conductor}/SKILL.md", StringComparison.Ordinal)).ToList();

                    if (conductorFiles.Count > 1)
                    {
                        throw new SquadApmValidationException(
                            $"Duplicate projection detected for '{conductor}' on native target '{token}'.");
                    }

                    if (conductorFiles.Any(f => f.RelativePath.Contains("/skills/", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new SquadApmValidationException(
                            $"Native target '{token}' emitted skill for primary agent '{conductor}', violating single-projection rule.");
                    }
                }
            }
            else
            {
                // Fallback target: must NOT emit native /agents/ files
                if (targetFiles.Any(f => f.RelativePath.Contains("/agents/", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new SquadApmValidationException(
                        $"Fallback target '{token}' emitted native agent file, which is not supported.");
                }

                // Conductor and conductor-v3 single projection rule on fallback target
                foreach (string conductor in SharedConductorIdentities)
                {
                    List<SquadDeploymentFile> conductorFiles = targetFiles.Where(f =>
                        f.RelativePath.Contains($"/{conductor}/SKILL.md", StringComparison.Ordinal)).ToList();

                    if (conductorFiles.Count > 1)
                    {
                        throw new SquadApmValidationException(
                            $"Duplicate skill projection detected for '{conductor}' on fallback target '{token}'.");
                    }
                }

                // Distinct-body collisions on fallback targets must have both canonical skill and role-prefixed skill
                foreach (string collision in DistinctBodyCollisions)
                {
                    if (source.Agents.Any(a => string.Equals(a.Name, collision, StringComparison.Ordinal)) &&
                        source.Skills.Any(s => string.Equals(s.Name, collision, StringComparison.Ordinal)))
                    {
                        bool hasCanonicalSkill = targetFiles.Any(f => f.RelativePath.Contains($"/{collision}/SKILL.md", StringComparison.Ordinal));
                        bool hasRoleSkill = targetFiles.Any(f => f.RelativePath.Contains($"/role-{collision}/SKILL.md", StringComparison.Ordinal));

                        if (!hasCanonicalSkill || !hasRoleSkill)
                        {
                            throw new SquadApmValidationException(
                                $"Fallback target '{token}' must contain both canonical skill '{collision}' and lowered role skill 'role-{collision}'.");
                        }
                    }
                }
            }
        }

        // 3. Validate degradation records and SHA-256 instruction-body digests
        foreach (ApmDegradationRecord degradation in result.Degradations)
        {
            if (!agentMap.TryGetValue(degradation.CanonicalIdentity, out SquadAgent? agent))
            {
                throw new SquadApmValidationException(
                    $"Degradation record references unknown agent '{degradation.CanonicalIdentity}'.");
            }

            if (string.IsNullOrWhiteSpace(degradation.InstructionDigest))
            {
                throw new SquadApmValidationException(
                    $"Degradation record for agent '{degradation.CanonicalIdentity}' is missing the instruction body digest.");
            }

            if (!string.Equals(degradation.InstructionDigest, agent.BodyDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new SquadApmValidationException(
                    $"Degradation record for agent '{degradation.CanonicalIdentity}' has mismatched instruction digest '{degradation.InstructionDigest}' (expected '{agent.BodyDigest}').");
            }

            if (string.Equals(degradation.Code, "widened", StringComparison.OrdinalIgnoreCase) ||
                (degradation.Details is not null && degradation.Details.Contains("widening", StringComparison.OrdinalIgnoreCase)))
            {
                throw new SquadApmValidationException(
                    $"Permission widening detected for agent '{degradation.CanonicalIdentity}'.");
            }
        }
    }
}
