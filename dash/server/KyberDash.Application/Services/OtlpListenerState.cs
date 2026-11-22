using KyberDash.Contracts.Services;

namespace KyberDash.Application;

/// <summary>
/// In-process record of the collector socket this host bound. It starts unset and becomes
/// listening only when the host calls <see cref="NoteListening"/> after Kestrel accepts the port.
/// </summary>
public class OtlpListenerState : IOtlpListenerState
{
    /// <inheritdoc />
    public bool IsListening { get; private set; }

    /// <inheritdoc />
    public int? BoundPort { get; private set; }

    /// <inheritdoc />
    public void NoteListening(int port)
    {
        if (port <= 0)
            throw new ArgumentOutOfRangeException(nameof(port), port, "A bound collector port is greater than zero.");

        BoundPort = port;
        IsListening = true;
    }
}
