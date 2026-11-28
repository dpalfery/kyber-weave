namespace KyberWeave.Contracts.Repositories;

/// <summary>
/// Runs every child through another <see cref="IProcessRunner"/>, bounding any request that names no timeout of its
/// own at a default. Callers whose children must never hang (doctor, run start, docs init, the MCP tooling check) wrap
/// the shared runner in one, so a git or CodeGraph call that passes no timeout is still killed at the bound.
/// </summary>
/// <param name="inner">The runner that starts the child.</param>
/// <param name="timeout">The bound for a request that names none.</param>
public sealed class DefaultTimeoutProcessRunner(IProcessRunner inner, TimeSpan timeout) : IProcessRunner
{
    private readonly IProcessRunner _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _inner.RunAsync(request with { Timeout = request.Timeout ?? timeout }, cancellationToken);
    }

    /// <inheritdoc />
    public bool IsAlive(int processId) => _inner.IsAlive(processId);

    /// <inheritdoc />
    public string? FindOnPath(string program) => _inner.FindOnPath(program);
}
