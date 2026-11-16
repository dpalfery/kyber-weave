using KyberWeave.Core.Arbiter.ModelTargets;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the model targets table a Squad render writes for one harness.</summary>
public interface IArbiterModelTargetsReader
{
    /// <summary>Reads <c>model-targets.&lt;harness&gt;.json</c> from the arbiter directory.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="harness">The harness token, which is also the file's name suffix.</param>
    /// <returns>The table, or null when the file is absent, unreadable, malformed, or not a valid v1 table for this harness.</returns>
    ArbiterModelTargets? TryRead(string arbiterDirectory, string harness);
}
