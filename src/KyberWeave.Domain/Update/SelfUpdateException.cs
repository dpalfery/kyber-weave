namespace KyberWeave.Domain.Update;

/// <summary>An operator-facing failure while resolving or replacing Release binaries.</summary>
public sealed class SelfUpdateException : Exception
{
    public SelfUpdateException()
    {
    }

    public SelfUpdateException(string message)
        : base(message)
    {
    }

    public SelfUpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

