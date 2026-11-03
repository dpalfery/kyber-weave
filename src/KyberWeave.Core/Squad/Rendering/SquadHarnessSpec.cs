using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// The facts about one harness that the shared render pipeline reads: which target it is, where
/// its files go, and the format decisions every renderer used to restate in its own code.
/// </summary>
/// <param name="Target">The harness target this spec describes.</param>
/// <param name="DisplayName">The name the wrong-target guard uses, e.g. <c>Command Code</c>.</param>
/// <param name="ProjectRoot">
/// The project-scope root directory, e.g. <c>.kilo</c>. Under global scope the physical root
/// already is the harness's global directory, so relative paths drop this segment.
/// </param>
/// <param name="AgentsSubdirectory">
/// Where agent files go beneath the root, or <see langword="null"/> for a harness with no agent
/// primitive.
/// </param>
/// <param name="ModelPolicy">How a <c>models.yml</c> entry lowers onto the harness's model key.</param>
/// <param name="DescriptionLineBreaks">How a multi-line description collapses onto one line.</param>
/// <remarks>
/// Native versus fallback projection is not a field here: it is a fact about the target, so it
/// lives on <see cref="SquadTargetCatalog.GetAgentProjection"/>, where the registry's validation
/// reads it for every renderer, including ones that do not use this pipeline.
/// </remarks>
internal sealed record SquadHarnessSpec(
    SquadTarget Target,
    string DisplayName,
    string ProjectRoot,
    string? AgentsSubdirectory,
    SquadModelPolicy ModelPolicy,
    SquadLineBreaks DescriptionLineBreaks = SquadLineBreaks.CarriageReturnOrLineFeed)
{
    /// <summary>The canonical target token every emitted file and record carries.</summary>
    public string Token { get; } = SquadTargetCatalog.GetToken(Target);

    /// <summary>
    /// Resolves <paramref name="subdirectory"/> for <paramref name="scope"/>: beneath
    /// <see cref="ProjectRoot"/> at project scope, bare at global scope.
    /// </summary>
    public string ResolveDirectory(string subdirectory, SquadDeploymentScope scope) =>
        scope == SquadDeploymentScope.Project ? $"{ProjectRoot}/{subdirectory}" : subdirectory;
}

/// <summary>How a harness lowers a model profile's entry onto its own model key.</summary>
/// <remarks>
/// Every member omits a profile <c>default</c> of <c>inherit</c>, so the harness picks its own
/// default. They differ only in how they treat the harness-specific entry.
/// </remarks>
internal enum SquadModelPolicy
{
    /// <summary>The harness has no model key; nothing is resolved.</summary>
    NotRendered,

    /// <summary>A harness-specific entry is emitted verbatim, <c>inherit</c> included.</summary>
    HarnessValueVerbatim,

    /// <summary><c>inherit</c> omits the key, whether it comes from the harness entry or the default.</summary>
    InheritOmitted,

    /// <summary><c>inherit</c> or a blank value omits the key, from either source.</summary>
    InheritOrBlankOmitted
}

/// <summary>Which characters split a multi-line scalar before it collapses onto one line.</summary>
/// <remarks>
/// The two only disagree on a bare carriage return in the middle of a line, which a YAML parser
/// produces only from an escaped <c>\r</c> in a double-quoted scalar. Both are kept so moving a
/// renderer onto the pipeline cannot change a byte; unifying them is a behaviour change.
/// </remarks>
internal enum SquadLineBreaks
{
    /// <summary>Split on either <c>\r</c> or <c>\n</c>.</summary>
    CarriageReturnOrLineFeed,

    /// <summary>Normalize <c>\r\n</c> to <c>\n</c> and split on <c>\n</c> only.</summary>
    LineFeedOnly
}

/// <summary>Model and scalar lowering shared by every harness spec.</summary>
internal static class SquadHarnessLowering
{
    private const string Inherit = "inherit";

    /// <summary>Resolves the model key value for <paramref name="agent"/>, or null to omit it.</summary>
    public static string? ResolveModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        string harnessToken,
        SquadModelPolicy policy)
    {
        if (policy == SquadModelPolicy.NotRendered ||
            !modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue(harnessToken, out string? harnessModel))
        {
            return policy == SquadModelPolicy.HarnessValueVerbatim ? harnessModel : OmitInherited(harnessModel, policy);
        }

        return OmitInherited(profile.Default, policy);
    }

    /// <summary>Collapses a multi-line scalar onto one line, dropping blank lines.</summary>
    public static string SingleLine(string value, SquadLineBreaks lineBreaks) =>
        lineBreaks == SquadLineBreaks.LineFeedOnly
            ? string.Join(
                ' ',
                value.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : string.Join(
                " ",
                value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? OmitInherited(string value, SquadModelPolicy policy) =>
        string.Equals(value, Inherit, StringComparison.Ordinal) ||
        (policy == SquadModelPolicy.InheritOrBlankOmitted && string.IsNullOrWhiteSpace(value))
            ? null
            : value;
}
