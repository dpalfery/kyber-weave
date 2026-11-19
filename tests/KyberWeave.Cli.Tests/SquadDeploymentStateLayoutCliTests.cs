using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The status command's per-target verification over an in-memory global deployment: its report is the command's own
/// output, so the case lives with the CLI tests even though its subject is the deployment layout.
/// </summary>
public sealed class SquadDeploymentStateLayoutCliTests
{
    /// <summary>
    ///     A global install across two targets that render the same relative path must deploy to
    ///     each target's own root, and status must verify the bytes there — not against the
    ///     deployment root, which holds none of the files.
    /// </summary>
    [Fact]
    public void ExecuteGlobalInstallSharingPathsAcrossTargetsDeploysAndStatusVerifiesPerTargetRoots()
    {
        InMemorySquadState state = new();
        string claudeRoot = Path.Combine(state.Files.Root, "claude-home", ".claude");
        string codexRoot = Path.Combine(state.Files.Root, "codex-home", ".codex");
        state.Files.CreateDirectory(claudeRoot);
        state.Files.CreateDirectory(codexRoot);
        FakeSquadGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Claude] = claudeRoot,
            [SquadTarget.Codex] = codexRoot
        });
        string deploymentRoot = state.Files.Root;
        SquadDeploymentPlan plan = state.Planner.CreateInstall(
            deploymentRoot,
            SquadDeploymentScope.Global,
            SquadStateTestLayout.Lock(),
            [
                new SquadDeploymentFile("agents/conductor.md", "claude conductor"u8.ToArray(), "claude"),
                new SquadDeploymentFile("agents/conductor.md", "codex conductor"u8.ToArray(), "codex")
            ],
            [],
            false,
            new FixedTimeProvider(SquadStateTestLayout.InstalledAt),
            globalRoots);

        state.Transaction().Execute(plan);

        Assert.Equal(
            "claude conductor",
            state.Files.ReadAllText(ToPlatformPath(claudeRoot, "agents/conductor.md")));
        Assert.Equal(
            "codex conductor",
            state.Files.ReadAllText(ToPlatformPath(codexRoot, "agents/conductor.md")));
        // A Global receipt always serializes on the v2 schema (A3), so what SquadTransaction
        // persisted and re-read differs from the plan's own v1-labeled in-memory receipt only
        // in that one field.
        SquadStateDocumentFixtures.AssertReceiptEqual(
            plan.Receipt with { Schema = SquadStateSchemas.ReceiptSchemaV2 },
            Assert.IsType<SquadReceipt>(state.Store.ReadReceipt(deploymentRoot, SquadDeploymentScope.Global)));

        SquadStatusCommand command = SquadTestCommands.InMemoryStatus(state, globalRoots);
        CapturedConsoleExecution<int> clean = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = deploymentRoot,
                Global = true
            }));
        Assert.Equal(0, clean.Result);
        Assert.Contains("(claude)", clean.Output, StringComparison.Ordinal);
        Assert.Contains("(codex)", clean.Output, StringComparison.Ordinal);

        state.Files.WriteAllText(
            ToPlatformPath(claudeRoot, "agents/conductor.md"),
            "operator edit");
        CapturedConsoleExecution<int> drifted = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = deploymentRoot,
                Global = true
            }));

        Assert.Equal(1, drifted.Result);
        Assert.Contains("modified locally", drifted.Output, StringComparison.Ordinal);
        Assert.Contains("(claude)", drifted.Output, StringComparison.Ordinal);
    }

    private static string ToPlatformPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
