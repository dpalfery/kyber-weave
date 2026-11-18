using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Defines the contract for lowering canonical Squad source into a coding harness's
/// native agent and skill files.
/// </summary>
/// <remarks>
/// This is the seam that used to be filled by shelling out to the upstream Agent Package
/// Manager (<c>apm compile</c>). No released APM version exposes the machine-readable
/// output this pipeline needs — 0.28.0 has no <c>--format json</c> mode at all, and three
/// of the ten approved targets (<c>kilo</c>, <c>warp</c>, <c>factory</c>) are absent from
/// its target matrix — so rendering is native Kyber-Weave code from here on.
/// </remarks>
public interface ISquadRenderer
{
    /// <summary>The targets this renderer can produce native output for.</summary>
    IReadOnlyCollection<SquadTarget> SupportedTargets { get; }

    /// <summary>Renders canonical Squad source into harness-native deployment files.</summary>
    Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default);
}
