using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The status command's drift report over an in-memory deployment: the report is the command's own output, so the case
/// lives with the CLI tests even though its subject is the deployment receipt.
/// </summary>
public sealed class SquadDeploymentStateUpdateCliTests
{
    private readonly InMemorySquadState _state = new();

    /// <summary>
    ///     Progressively disclosed references are ordinary deployment files. Their rendered paths
    ///     must therefore receive the same exact-byte ownership and status guarantees as principals.
    /// </summary>
    [Fact]
    public void RenderedResourcesAreReceiptOwnedWithExactHashesAndStatusDetectsTheirDrift()
    {
        const string agentResource = ".codex/agents/conductor/references/plan-path.md";
        const string skillResource = ".codex/skills/product-owner/references/requirements.md";
        const string agentContent = "# Plan path\n";
        const string skillContent = "# Requirements phase\n";
        string root = _state.CreateRoot("rendered-resources");
        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [
                Rendered(agentResource, agentContent),
                Rendered(skillResource, skillContent)
            ],
            [],
            false,
            _state.Time);

        _state.Transaction().Execute(plan);

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(_state.Store.ReadReceipt(
            root,
            SquadDeploymentScope.Project));
        Assert.Equal(
            [agentResource, skillResource],
            receipt.Files.Select(file => file.RelativePath));
        Assert.Equal(
            [Digest(agentContent), Digest(skillContent)],
            receipt.Files.Select(file => file.Sha256));
        Assert.All(receipt.Files, file => Assert.Equal("codex", file.Target));

        SquadStatusCommand command = SquadTestCommands.InMemoryStatus(_state);
        CapturedConsoleExecution<int> clean = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = root,
                Global = false
            }));
        Assert.Equal(0, clean.Result);

        Write(root, agentResource, "operator edit\n");
        CapturedConsoleExecution<int> drifted = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = root,
                Global = false
            }));

        Assert.Equal(1, drifted.Result);
        Assert.Contains(agentResource, drifted.Output, StringComparison.Ordinal);
        Assert.Contains("modified locally", drifted.Output, StringComparison.Ordinal);
    }

    private static SquadLock Lock(string version = "1.2.3") =>
        new(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["codex", "warp"],
            ["cursor"],
            "best-effort",
            Digest("bundle"),
            Digest("asset"),
            new SquadApmIdentity(version, "0123456789abcdef", Digest("apm")));

    private static SquadDeploymentFile Rendered(string relativePath, string content, string target = "codex") =>
        new(relativePath, Encoding.UTF8.GetBytes(content), target);

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _state.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _state.Files.WriteAllText(path, content);
    }
}
