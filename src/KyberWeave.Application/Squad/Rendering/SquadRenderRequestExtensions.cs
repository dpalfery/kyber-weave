using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>What a renderer asks of a render request: each input it needs, or why the request cannot supply it.</summary>
internal static class SquadRenderRequestExtensions
{
    /// <summary>Gets the canonical source this render starts from, or throws why it is not available.</summary>
    /// <param name="request">The render request.</param>
    /// <returns>The preloaded source.</returns>
    /// <exception cref="SquadRenderValidationException">The request was never prepared with the source.</exception>
    public static SquadSource RequireSource(this SquadRenderRequest request) =>
        request.PreloadedSource ?? throw (request.DiskInputs?.SourceFailure ?? Unprepared("the canonical source"));

    /// <summary>Gets the worker baseline block, or throws why it could not be read.</summary>
    /// <param name="request">The render request.</param>
    /// <returns>The block, or null when the tree carries no baseline.</returns>
    /// <exception cref="SquadRenderValidationException">The request was never prepared, or the baseline is malformed.</exception>
    public static string? RequireWorkerBaselineBlock(this SquadRenderRequest request)
    {
        SquadRenderDiskInputs inputs = request.RequireDiskInputs();
        return inputs.WorkerBaselineFailure is { } failure ? throw failure : inputs.WorkerBaselineBlock;
    }

    /// <summary>Gets the reader of environment variables this render consults, or throws that the request was never prepared.</summary>
    /// <param name="request">The render request.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="SquadRenderValidationException">The request carries no reader.</exception>
    public static Func<string, string?> RequireEnvironmentReader(this SquadRenderRequest request) =>
        request.EnvironmentReader ?? throw Unprepared("an environment reader");

    /// <summary>Gets what the host read from disk ahead of the render, or throws that the request was never prepared.</summary>
    /// <param name="request">The render request.</param>
    /// <returns>The disk inputs.</returns>
    /// <exception cref="SquadRenderValidationException">The request was never prepared.</exception>
    public static SquadRenderDiskInputs RequireDiskInputs(this SquadRenderRequest request) =>
        request.DiskInputs ?? throw Unprepared("what the render reads from disk");

    private static SquadRenderValidationException Unprepared(string what) =>
        new($"The render request does not carry {what}: prepare it with the render input reader before rendering.");
}
