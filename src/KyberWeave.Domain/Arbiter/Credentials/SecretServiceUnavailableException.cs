namespace KyberWeave.Core.Arbiter.Credentials;

/// <summary>
/// The Secret Service tool cannot be started, so no key can be read or stored on this host.
/// </summary>
/// <remarks>
/// Raised when <c>secret-tool</c> is not installed, the usual case on a Linux host without
/// <c>libsecret-tools</c>. The message is the operator's next step, and it names the
/// environment variable as the alternative. The failure stays fail-closed: a missing tool
/// is not treated as an absent key, because an undecidable step-1 answer maps to allow on
/// review triggers and would let an unverified review through.
/// </remarks>
public sealed class SecretServiceUnavailableException : InvalidOperationException
{
    /// <summary>The one-line message every missing-tool failure carries.</summary>
    public const string ActionableMessage =
        "A remote provider needs a key: set the TYPESAFE_API_KEY environment variable, " +
        "or install libsecret-tools (the package that provides secret-tool) and store the key " +
        "with 'kyber-weave arbiter setup'.";

    /// <summary>Creates the failure with the actionable message.</summary>
    public SecretServiceUnavailableException()
        : base(ActionableMessage)
    {
    }

    /// <summary>Creates the failure with an explicit message.</summary>
    public SecretServiceUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the failure, keeping the process-start exception as its cause.</summary>
    public SecretServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the failure with the actionable message and the process-start exception as its cause.</summary>
    public SecretServiceUnavailableException(Exception innerException)
        : base(ActionableMessage, innerException)
    {
    }
}
