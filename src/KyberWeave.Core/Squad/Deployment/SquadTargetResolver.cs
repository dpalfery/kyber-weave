namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The lifecycle operation requesting target resolution.</summary>
public enum SquadTargetOperation
{
    Install,
    Update,
    Uninstall
}

/// <summary>The outcome category produced by target resolution.</summary>
public enum SquadTargetResolutionKind
{
    Resolved,
    InteractiveSelectionRequired,
    Failure
}

/// <summary>The highest-precedence source that selected the target set.</summary>
public enum SquadTargetResolutionSource
{
    None,
    Explicit,
    Configuration,
    Receipt,
    Markers
}

/// <summary>Inputs to the pure Squad target-selection decision.</summary>
public sealed class SquadTargetResolutionRequest
{
    public required string RootPath { get; init; }

    public SquadTargetOperation Operation { get; init; }

    public IReadOnlyList<string> ExplicitTargets { get; init; } = Array.Empty<string>();

    public IReadOnlyList<SquadTarget> ConfiguredTargets { get; init; } = Array.Empty<SquadTarget>();

    public IReadOnlyList<SquadTarget> ReceiptTargets { get; init; } = Array.Empty<SquadTarget>();

    public IReadOnlyList<string> ExplicitExclusions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<SquadTarget> ConfiguredExclusions { get; init; } = Array.Empty<SquadTarget>();

    public bool IsInteractive { get; init; }
}

/// <summary>A target-selection decision for the CLI host to render or act upon.</summary>
public sealed class SquadTargetResolutionDecision
{
    public required SquadTargetResolutionKind Kind { get; init; }

    public required IReadOnlyList<SquadTarget> Targets { get; init; }

    public required SquadTargetResolutionSource Source { get; init; }

    public int? ExitCode { get; init; }

    public string? RecoveryCommand { get; init; }
}

/// <summary>Resolves Squad targets without prompting or writing global console state.</summary>
public static class SquadTargetResolver
{
    private static readonly IReadOnlyDictionary<SquadTarget, IReadOnlyList<StrongMarker>> Markers =
        new Dictionary<SquadTarget, IReadOnlyList<StrongMarker>>
        {
            [SquadTarget.Codex] = [new(".codex", true)],
            [SquadTarget.Cursor] = [new(".cursor", true)],
            [SquadTarget.Claude] = [new(".claude", true)],
            [SquadTarget.Copilot] =
            [
                new(".github/copilot-instructions.md", false),
                new(".github/instructions", true),
                new(".github/agents", true),
                new(".github/prompts", true),
                new(".github/hooks", true)
            ],
            [SquadTarget.OpenCode] = [new(".opencode", true)],
            [SquadTarget.Kilo] = [new(".kilo", true)],
            [SquadTarget.Gemini] = [new(".gemini", true)],
            [SquadTarget.Warp] = [new(".warp", true)],
            [SquadTarget.Factory] = [new(".factory", true)]
        };

    /// <summary>Resolves targets according to the operation-specific precedence contract.</summary>
    public static SquadTargetResolutionDecision Resolve(SquadTargetResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);

        (IReadOnlyList<SquadTarget>? targets, SquadTargetResolutionSource source) = SelectTargets(request);
        if (targets.Count > 0 && request.Operation == SquadTargetOperation.Install)
            targets = ApplyExclusions(targets, request);

        if (targets.Count > 0)
            return Resolved(targets, source);

        return request.IsInteractive
            ? new SquadTargetResolutionDecision
            {
                Kind = SquadTargetResolutionKind.InteractiveSelectionRequired,
                Targets = Array.Empty<SquadTarget>(),
                Source = SquadTargetResolutionSource.None
            }
            : new SquadTargetResolutionDecision
            {
                Kind = SquadTargetResolutionKind.Failure,
                Targets = Array.Empty<SquadTarget>(),
                Source = SquadTargetResolutionSource.None,
                ExitCode = 2,
                RecoveryCommand = GetRecoveryCommand(request.Operation)
            };
    }

    private const string InstallRecoveryCommand = "kyber-weave squad install --target <target>";
    private const string UpdateRecoveryCommand = "kyber-weave squad update --target <target>";
    private const string UninstallRecoveryCommand = "kyber-weave squad uninstall --target <target>";

    private static string GetRecoveryCommand(SquadTargetOperation operation) => operation switch
    {
        SquadTargetOperation.Update => UpdateRecoveryCommand,
        SquadTargetOperation.Uninstall => UninstallRecoveryCommand,
        _ => InstallRecoveryCommand
    };

    private static (IReadOnlyList<SquadTarget> Targets, SquadTargetResolutionSource Source)
        SelectTargets(SquadTargetResolutionRequest request)
    {
        if (request.Operation is SquadTargetOperation.Update or SquadTargetOperation.Uninstall)
        {
            return request.ReceiptTargets.Count > 0
                ? (OrderedSet(request.ReceiptTargets), SquadTargetResolutionSource.Receipt)
                : (Array.Empty<SquadTarget>(), SquadTargetResolutionSource.None);
        }

        if (request.ExplicitTargets.Count > 0)
            return (SquadTargetCatalog.Parse(request.ExplicitTargets), SquadTargetResolutionSource.Explicit);

        if (request.ConfiguredTargets.Count > 0)
            return (OrderedSet(request.ConfiguredTargets), SquadTargetResolutionSource.Configuration);

        IReadOnlyList<SquadTarget> detected = DetectMarkers(request.RootPath);
        return detected.Count > 0
            ? (detected, SquadTargetResolutionSource.Markers)
            : (Array.Empty<SquadTarget>(), SquadTargetResolutionSource.None);
    }

    private static IReadOnlyList<SquadTarget> ApplyExclusions(
        IReadOnlyList<SquadTarget> targets,
        SquadTargetResolutionRequest request)
    {
        HashSet<SquadTarget> excluded = new HashSet<SquadTarget>(request.ConfiguredExclusions);
        excluded.UnionWith(SquadTargetCatalog.Parse(request.ExplicitExclusions));
        return targets.Where(target => !excluded.Contains(target)).ToArray();
    }

    private static IReadOnlyList<SquadTarget> DetectMarkers(string rootPath)
    {
        string root = Path.GetFullPath(rootPath);
        List<SquadTarget> detected = new List<SquadTarget>();
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            if (!Markers.TryGetValue(target, out IReadOnlyList<StrongMarker>? markers))
                continue;

            if (markers.Any(marker => marker.ExistsUnder(root)))
                detected.Add(target);
        }

        return detected;
    }

    private static IReadOnlyList<SquadTarget> OrderedSet(IEnumerable<SquadTarget> targets)
    {
        HashSet<SquadTarget> seen = new HashSet<SquadTarget>();
        return targets.Where(seen.Add).ToArray();
    }

    private static SquadTargetResolutionDecision Resolved(
        IReadOnlyList<SquadTarget> targets,
        SquadTargetResolutionSource source) =>
        new()
        {
            Kind = SquadTargetResolutionKind.Resolved,
            Targets = targets,
            Source = source
        };

    private sealed record StrongMarker(string RelativePath, bool IsDirectory)
    {
        public bool ExistsUnder(string rootPath)
        {
            string path = Path.Combine(
                rootPath,
                RelativePath.Replace('/', Path.DirectorySeparatorChar));
            return IsDirectory ? Directory.Exists(path) : File.Exists(path);
        }
    }
}
