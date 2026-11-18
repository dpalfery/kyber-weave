using KyberWeave.Application.Review;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Data.Review;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>The test command a task's packet prints, for the callers that have no container.</summary>
/// <remarks>
/// The section is built by <see cref="PacketTestCommandRenderer"/> in the application layer. This answers over the real
/// file system, and stays for <c>docs packet</c> and the tests that call it statically. Delete it when those take the
/// services from the container.
/// </remarks>
public static class PacketTestCommand
{
    /// <summary>The heading the printed section starts with.</summary>
    public const string Heading = PacketTestCommandRenderer.Heading;

    private static readonly IPlanTaskTargetService Targets = new PlanTaskTargetService(
        new PlanTaskService(LegacyPorts.Files),
        new TaskGateSelectionService(new FileRepositoryTreeProbe(), new FileTestClassSources()),
        new GitFacts());

    private static readonly ITaskGateSelectionService TaskGates =
        new TaskGateSelectionService(new FileRepositoryTreeProbe(), new FileTestClassSources());

    /// <inheritdoc cref="PacketTestCommandRenderer.Render" />
    public static string? Render(
        string repoRoot,
        KyberWeaveConfig config,
        string? agent,
        PlanTaskFileEntry entry,
        PlanContractRow? row,
        bool windows) =>
        PacketTestCommandRenderer.Render(repoRoot, config, agent, entry, row, windows, Targets, TaskGates);

    /// <inheritdoc cref="PacketTestCommandRenderer.GateCommand" />
    public static string GateCommand(string agent, string? taskId) =>
        PacketTestCommandRenderer.GateCommand(agent, taskId);

    /// <summary>The argument as a shell reads it back unchanged: bare when it holds only safe characters, else quoted.</summary>
    internal static string Quote(string element, bool windows) =>
        PacketTestCommandRenderer.Quote(element, windows);
}
