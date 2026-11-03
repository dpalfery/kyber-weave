namespace KyberWeave.Core.Arbiter.Credentials;

/// <summary>
/// The macOS keychain lookup failed for a reason other than "no entry", so the key's
/// absence cannot be concluded.
/// </summary>
/// <remarks>
/// <c>security find-generic-password</c> separates the two outcomes by exit status: 44
/// (errSecItemNotFound) means nothing matched, while any other non-zero status means the
/// lookup itself did not work. Collapsing both into "no key" reports an absent key for a
/// keychain that is locked or refusing access, and the operator is then told to store a
/// key that is already stored. Raising keeps that failure visible; the message carries the
/// exit code so the underlying cause can be looked up.
/// </remarks>
public sealed class KeychainUnavailableException : InvalidOperationException
{
    /// <summary>Creates the failure with no detail.</summary>
    public KeychainUnavailableException()
        : base("The macOS keychain read failed.")
    {
    }

    /// <summary>Creates the failure with an explicit message.</summary>
    public KeychainUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the failure, keeping the cause as its inner exception.</summary>
    public KeychainUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
