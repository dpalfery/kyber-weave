using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Data.Doctor;

/// <summary>Starts an MCP server child as a <see cref="StdioSession"/>.</summary>
public sealed class StdioMcpSessionFactory(TimeProvider time) : IMcpStdioSessionFactory
{
    /// <inheritdoc />
    public IMcpStdioSession Start(string executable, IReadOnlyList<string> arguments, TimeSpan budget) =>
        new Session(executable, arguments, budget, time);

    private sealed class Session : IMcpStdioSession
    {
        private readonly StdioSession inner;

        public Session(string executable, IReadOnlyList<string> arguments, TimeSpan budget, TimeProvider time) =>
            inner = new StdioSession(executable, arguments, budget, time);

        public string? FirstErrorLine => inner.FirstErrorLine;

        public Task<string?> WaitForFirstErrorLineAsync(TimeSpan wait, CancellationToken cancellationToken = default) =>
            inner.WaitForFirstErrorLineAsync(wait, cancellationToken);

        public void SendLine(string line) => inner.SendLine(line);

        public Task<string> ReadLineAsync(CancellationToken cancellationToken = default) =>
            inner.ReadLineAsync(cancellationToken);

        public void Dispose() => inner.Dispose();
    }
}
