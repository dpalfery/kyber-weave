using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Loads canonical Squad source for a render execution, applying any target-scoped model preset overrides.
/// </summary>
/// <remarks>
/// This seam isolates model preset application from the individual renderer implementations,
/// ensuring all 13 renderers receive the effectively configured model profiles consistently
/// without mutating on-disk canonical files or re-implementing preset merge logic.
/// </remarks>
internal static class SquadRenderSource
{
    /// <summary>
    /// Loads canonical Squad source and applies the model preset for <paramref name="target"/> if present.
    /// </summary>
    /// <param name="request">The active render request containing source path and preset configuration.</param>
    /// <param name="target">The specific harness target being rendered.</param>
    /// <returns>A <see cref="SquadSource"/> whose model profiles reflect any active preset overrides.</returns>
    public static SquadSource Load(SquadRenderRequest request, SquadTarget target)
    {
        ArgumentNullException.ThrowIfNull(request);

        SquadSource source = SquadSourceLoader.Load(request.SourceDirectory);
        if (request.ModelPresets != null && request.ModelPresets.TryGetValue(target, out SquadModelPreset? preset) && preset is not null)
        {
            return source with { ModelProfiles = preset.ApplyTo(source.ModelProfiles) };
        }

        return source;
    }
}
