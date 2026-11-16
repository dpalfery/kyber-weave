namespace KyberWeave.Contracts.Repositories;

/// <summary>
/// The per-subagent budget files under <c>~/.kyber-weave/arbiter/budgets</c>: one append-only file per subagent,
/// a header line and then one timestamp per call, so parallel hook processes never lose a count.
/// </summary>
public interface IWorkerBudgetStore
{
    /// <summary>Creates the budget directory when it is missing.</summary>
    /// <param name="home">The operator's home.</param>
    void EnsureDirectory(string home);

    /// <summary>Whether a budget file exists.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The subagent's key.</param>
    bool Exists(string home, string key);

    /// <summary>Reads every line of a budget file.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The subagent's key.</param>
    /// <returns>The lines; empty when the file is missing.</returns>
    string[] ReadLines(string home, string key);

    /// <summary>Deletes a budget file.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The subagent's key.</param>
    void Delete(string home, string key);

    /// <summary>Creates a budget file holding <paramref name="content"/>, unless another process created it first.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The subagent's key.</param>
    /// <param name="content">The header line, with its line ending.</param>
    void TryCreate(string home, string key, string content);

    /// <summary>Appends one line to a budget file.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The subagent's key.</param>
    /// <param name="content">The line, with its line ending.</param>
    void Append(string home, string key, string content);

    /// <summary>Deletes the budget files not written to since the retention window.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="now">The time the window is measured from.</param>
    /// <param name="retention">How long a budget file is kept after its last write.</param>
    void Sweep(string home, DateTimeOffset now, TimeSpan retention);
}
