using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The one fake for <see cref="IMcpStdioSessionFactory"/>: starts no process and answers each read from lines the test
/// scripted, so a unit test can put any server behaviour in front of the code that talks to one.
/// </summary>
/// <remarks>
/// A reply is either a line, or an exception the read throws (a timeout, a closed stream, an over-long line). The
/// factory records the executable, arguments and budget the session was started with, every line sent, and whether the
/// session was disposed, which is when the real session kills the child's process tree.
/// </remarks>
internal sealed class FakeMcpStdioSessionFactory : IMcpStdioSessionFactory
{
    private readonly Queue<object> _replies = new();

    /// <summary>Gets the executable the session was started with, or null when none was.</summary>
    public string? Executable { get; private set; }

    /// <summary>Gets the arguments the session was started with.</summary>
    public IReadOnlyList<string> Arguments { get; private set; } = [];

    /// <summary>Gets the budget the session was started with.</summary>
    public TimeSpan? Budget { get; private set; }

    /// <summary>Gets every line the probe sent, in order.</summary>
    public List<string> Sent { get; } = [];

    /// <summary>Gets whether the session was disposed, which kills the real child.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Gets or sets the first line the child wrote to standard error.</summary>
    public string? ErrorLine { get; set; }

    /// <summary>Gets or sets the failure the start throws, as an executable that cannot be started does.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>Gets or sets a failure the n-th send (1-based) throws, as a closed input pipe does.</summary>
    public (int Number, Exception Failure)? SendFailure { get; set; }

    /// <summary>Scripts the next line the child writes to standard output.</summary>
    /// <param name="line">The line.</param>
    /// <returns>This factory.</returns>
    public FakeMcpStdioSessionFactory Reply(string line)
    {
        _replies.Enqueue(line);
        return this;
    }

    /// <summary>Scripts a read that fails with <paramref name="failure"/>.</summary>
    /// <param name="failure">The exception the read throws.</param>
    /// <returns>This factory.</returns>
    public FakeMcpStdioSessionFactory ReadFails(Exception failure)
    {
        _replies.Enqueue(failure);
        return this;
    }

    /// <inheritdoc />
    public IMcpStdioSession Start(string executable, IReadOnlyList<string> arguments, TimeSpan budget)
    {
        if (StartFailure is not null)
            throw StartFailure;

        Executable = executable;
        Arguments = [.. arguments];
        Budget = budget;
        return new Session(this);
    }

    private sealed class Session(FakeMcpStdioSessionFactory owner) : IMcpStdioSession
    {
        public string? FirstErrorLine => owner.ErrorLine;

        public string? WaitForFirstErrorLine(TimeSpan wait) => owner.ErrorLine;

        public void SendLine(string line)
        {
            owner.Sent.Add(line);
            if (owner.SendFailure is { } failure && failure.Number == owner.Sent.Count)
                throw failure.Failure;
        }

        public string ReadLine()
        {
            if (owner._replies.Count == 0)
                throw new EndOfStreamException("The scripted server has nothing more to say.");

            object next = owner._replies.Dequeue();
            return next is Exception failure ? throw failure : (string)next;
        }

        public void Dispose() => owner.Disposed = true;
    }
}
