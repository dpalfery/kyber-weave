using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Contracts.Docs;

/// <summary>
/// Keeps the CodeGraph index the docs tools answer from as fresh as their callers need, and puts a CodeGraph failure in
/// front of them in the form they act on.
/// </summary>
public interface ICodeGraphFreshnessGate
{
    /// <summary>The gate's verdict for this call: no refresh due, the refresh is someone else's, or a failure to report.</summary>
    /// <param name="resolverFactory">Builds the CodeGraph port the docs tools answer from.</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    Task<CodeGraphGate> EvaluateAsync(
        Func<ICodeGraphResolver> resolverFactory,
        CancellationToken cancellationToken = default);
}
