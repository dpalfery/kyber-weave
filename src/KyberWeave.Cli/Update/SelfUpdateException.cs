namespace KyberWeave.Cli.Update;

/// <summary>An operator-facing failure while resolving or replacing Release binaries.</summary>
internal sealed class SelfUpdateException : Exception
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

