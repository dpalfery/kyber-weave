using System.Globalization;

namespace KyberWeave.Contracts.Host;

/// <summary>The lease could not be taken because another owner held it for the whole timeout.</summary>
/// <remarks>
/// It derives from <see cref="InvalidOperationException"/> because that is what the Squad deployment's own lease
/// reports today, so a caller moving onto <see cref="ILease"/> keeps the exception it already handles.
/// </remarks>
public sealed class LeaseTimeoutException : InvalidOperationException
{
    /// <summary>Creates the exception with no lease detail.</summary>
    public LeaseTimeoutException()
    {
    }

    /// <summary>Creates the exception with the message given.</summary>
    /// <param name="message">The message.</param>
    public LeaseTimeoutException(string? message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the message and inner exception given.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public LeaseTimeoutException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a lease that stayed held.</summary>
    /// <param name="name">The lease name.</param>
    /// <param name="timeout">The wait that elapsed.</param>
    public LeaseTimeoutException(string name, TimeSpan timeout)
        : this(name, timeout, null)
    {
    }

    /// <summary>Creates the exception for a lease the platform refused to open.</summary>
    /// <param name="name">The lease name.</param>
    /// <param name="timeout">The wait that elapsed.</param>
    /// <param name="innerException">The operating-system failure, when there is one.</param>
    public LeaseTimeoutException(string name, TimeSpan timeout, Exception? innerException)
        : base(FormatMessage(name, timeout), innerException)
    {
        Name = name;
        Timeout = timeout;
    }

    /// <summary>Gets the lease name that was not taken.</summary>
    public string Name { get; } = string.Empty;

    /// <summary>Gets the wait that elapsed.</summary>
    public TimeSpan Timeout { get; }

    private static string FormatMessage(string name, TimeSpan timeout) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "The lease '{0}' was not released within {1} ms.",
            name,
            timeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture));
}
