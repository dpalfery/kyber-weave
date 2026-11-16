using KyberWeave.Arbiter.Mcp;

namespace KyberWeave.Contracts.Services;

/// <summary>Builds what one <c>serve</c> process answers with: the root it serves, the hook's own decision engine and the readiness checks.</summary>
public interface IArbiterServeContextFactory
{
    /// <summary>Resolves the root from the arguments, the environment and the working directory, and builds the context for it.</summary>
    /// <param name="args">The serve process's arguments, which may carry <c>--repo-root</c>.</param>
    /// <param name="log">Diagnostics sink: stderr in production, never stdout.</param>
    /// <returns>The context.</returns>
    /// <exception cref="ArgumentException">A <c>--repo-root</c> flag carries no path.</exception>
    /// <exception cref="InvalidOperationException">The resolved root is not a directory.</exception>
    ArbiterServeContext Create(IReadOnlyList<string> args, TextWriter log);
}
