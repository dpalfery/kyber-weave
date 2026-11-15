using KyberDash.Domain;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Publication policy for the engine descriptor. The default writes <c>engine.json</c> atomically with
/// owner-only permissions; <see cref="ServerStateRepository"/> passes itself so its symlink refusal and
/// file mode apply to the engine descriptor too.
/// </summary>
internal interface IEngineDescriptorPublisher
{
    /// <summary>Writes the descriptor into <paramref name="stateDir"/>.</summary>
    /// <param name="stateDir">State directory holding <c>engine.json</c>.</param>
    /// <param name="descriptor">Descriptor to publish.</param>
    void WriteEngineDescriptor(string stateDir, EngineDescriptor descriptor);
}
