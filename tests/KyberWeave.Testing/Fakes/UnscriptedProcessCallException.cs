namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Thrown when a scripted process fake is asked for a call it was not told to expect.
/// </summary>
/// <remarks>
/// A dedicated type, not <see cref="InvalidOperationException"/>: production code catches that one where an absent
/// resource must read as an absent fact, so a missing script would turn into a silently passing test. No production
/// catch names this type, so the mistake always fails the test that made it.
/// </remarks>
public sealed class UnscriptedProcessCallException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public UnscriptedProcessCallException()
    {
    }

    /// <summary>Creates the exception for one unscripted call.</summary>
    /// <param name="message">The program and arguments no script matched.</param>
    public UnscriptedProcessCallException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception for one unscripted call, wrapping the failure that exposed it.</summary>
    /// <param name="message">The program and arguments no script matched.</param>
    /// <param name="innerException">The underlying failure.</param>
    public UnscriptedProcessCallException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
