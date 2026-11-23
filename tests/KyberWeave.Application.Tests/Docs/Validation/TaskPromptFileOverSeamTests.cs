using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Validation;

/// <summary>
/// The fallback harness's prompt file is written through the writable file-system seam, so a build runs over a
/// checkout held in memory and a failed write is a refusal rather than a prompt dispatched without its file.
/// </summary>
public sealed class TaskPromptFileOverSeamTests
{
    private const string Root = "/repo";

    private readonly InMemoryFileSystem _disk = new();

    [Fact]
    public void AFallbackHarnessGetsItsPromptFileInTheInMemoryCheckout()
    {
        TaskPromptResult result = Build("warp");

        Assert.True(result.Built, result.Refusal);
        string expected = $"{TaskPromptService.ScratchFolder}/csharp/{TaskPromptService.PromptFileName}";
        Assert.Equal(expected, result.PromptFile);
        Assert.Equal(result.Prompt, _disk.ReadAllText(Root + "/" + expected));
    }

    [Fact]
    public void AHarnessThatTheDispatchHookRewritesWritesNoFile()
    {
        TaskPromptResult result = Build("claude");

        Assert.True(result.Built, result.Refusal);
        Assert.Null(result.PromptFile);
        Assert.False(_disk.PathExists(Root + "/" + TaskPromptService.ScratchFolder));
    }

    [Fact]
    public void AFileThatCannotBeWrittenIsARefusalNamingTheFile()
    {
        _disk.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.WriteAllText));

        TaskPromptResult result = Build("warp");

        Assert.False(result.Built);
        Assert.Equal(TaskPromptRefusalKind.Finding, result.RefusalKind);
        Assert.Contains("could not be written", result.Refusal, StringComparison.Ordinal);
        Assert.Contains($"{TaskPromptService.ScratchFolder}/csharp/{TaskPromptService.PromptFileName}", result.Refusal, StringComparison.Ordinal);
        Assert.Null(result.PromptFile);
    }

    private TaskPromptResult Build(string harness) =>
        new TaskPromptService(
            new UnusedTasks(),
            new UnusedHostFacts(),
            _disk,
            _ => null,
            new FakeProcessEnvironment(currentDirectory: Root))
        .Build(new TaskPromptRequest(Root, Technology: "csharp", Harness: harness), KyberWeaveConfigAssembler.FromYaml("ontology:\n  docs-root: docs\n"));

    private sealed class UnusedTasks : IPlanTaskService
    {
        public IReadOnlyList<string> ActivePlans(string repoRoot, KyberWeaveConfig config) => throw new NotSupportedException();

        public IReadOnlyList<string> ReviewPlans(string repoRoot, KyberWeaveConfig config) => throw new NotSupportedException();

        public PlanTaskFileOutcome Run(
            string repoRoot,
            KyberWeaveConfig config,
            string artifactPath,
            PlanTaskFileMode mode,
            IPlanTaskAgentPicker? picker = null) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedHostFacts : IPacketHostFacts
    {
        public string Render(string repoRoot, KyberWeaveConfig config, string? role, string? taskId, IList<string>? diagnostics = null, string? technology = null) =>
            throw new NotSupportedException();
    }
}
