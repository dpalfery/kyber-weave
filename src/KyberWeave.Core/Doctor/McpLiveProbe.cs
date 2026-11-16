using KyberWeave.Application.Doctor;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Core.Doctor;

/// <summary>The live MCP probe's static entry point, for callers that have no container.</summary>
/// <remarks>The logic is <see cref="McpServerLiveProbe"/> in the application layer.</remarks>
public static class McpLiveProbe
{
    /// <inheritdoc cref="McpServerLiveProbe.Run"/>
    public static IReadOnlyList<DoctorCheck> Run(string root, DoctorOptions options) =>
        DoctorServices.McpProbe.Run(root, options);
}
