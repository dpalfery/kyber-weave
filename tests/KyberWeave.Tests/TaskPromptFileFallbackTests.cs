using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fixtures;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>
/// Where a harness's dispatch hook cannot rewrite the call, the prompt is written to one untracked file under the
/// run's scratch folder and the dispatch names it. A harness whose hook does rewrite the call writes no file: the
/// prompt travels inside the dispatch instead.
/// </summary>
/// <remarks>
/// The file is the whole delivery on a fallback harness, so it holds exactly the text the service built, one file
/// per task, under the folder the repository already ignores.
/// </remarks>
public sealed class TaskPromptFileFallbackTests : IDisposable
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private readonly DocFixture _fixture = DocsTaskPromptToolTests.Ready();

    public void Dispose() => _fixture.Dispose();

    private KyberWeaveConfig Config => DocsTaskPromptToolTests.Config(_fixture);

    private TaskPromptResult Build(string? harness) => DocsTaskPromptToolTests.Service(_fixture).Build(
        new TaskPromptRequest(_fixture.Root, TaskId: "T1", Harness: harness),
        Config);

    private string PromptPath() => Path.Combine(_fixture.Root, ".agent_scratchpad", "t1", "prompt.md");

    [Fact]
    public void AFallbackHarnessWritesThePromptItBuiltToTheRunsScratchFolder()
    {
        TaskPromptResult result = Build("warp");

        Assert.True(result.Built, result.Refusal);
        Assert.Equal(".agent_scratchpad/t1/prompt.md", result.PromptFile);
        string written = File.ReadAllText(PromptPath());
        Assert.Equal(result.Prompt, written);
        Assert.StartsWith("<task>", written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("copilot-cli")]
    [InlineData("codex")]
    [InlineData("cursor")]
    [InlineData("opencode")]
    public void ARewriteHarnessWritesNoFile(string harness)
    {
        TaskPromptResult result = Build(harness);

        Assert.True(result.Built, result.Refusal);
        Assert.Null(result.PromptFile);
        Assert.False(File.Exists(PromptPath()), $"'{harness}' rewrites the dispatch, so it must not get a prompt file.");
    }

    /// <summary>
    /// A dispatch that names only a technology has no task folder, so its fallback file is keyed by the technology.
    /// Without it the dispatch would claim the hook inserts a prompt that no harness rewrites.
    /// </summary>
    [Fact]
    public void ATechnologyOnlyDispatchOnAFallbackHarnessWritesItsPromptFile()
    {
        TaskPromptResult result = DocsTaskPromptToolTests.Service(_fixture).Build(
            new TaskPromptRequest(_fixture.Root, TaskId: null, Technology: "csharp", Harness: "warp"),
            Config);

        Assert.True(result.Built, result.Refusal);
        string expected = $"{TaskPromptService.ScratchFolder}/csharp/{TaskPromptService.PromptFileName}";
        Assert.Equal(expected, result.PromptFile);
        Assert.Equal(result.Prompt, File.ReadAllText(Path.Combine(_fixture.Root, expected)));
    }

    [Fact]
    public void TheToolNamesTheFileForATechnologyOnlyDispatchOnAFallbackHarness()
    {
        string reply = DocsTaskPromptToolTests.Tools(_fixture)
            .BuildTaskPrompt(taskId: null, technology: "csharp", harness: "warp");

        Assert.Contains(
            $"prompt file: {TaskPromptService.ScratchFolder}/csharp/{TaskPromptService.PromptFileName}",
            reply,
            StringComparison.Ordinal);
        Assert.Contains("read it first", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("the dispatch hook inserts it", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallerThatNamesNoHarnessWritesNoFile()
    {
        // docs packet and the dispatch hook build the same prompt and have no file to name.
        TaskPromptResult result = Build(harness: null);

        Assert.True(result.Built, result.Refusal);
        Assert.Null(result.PromptFile);
        Assert.False(File.Exists(PromptPath()));
    }

    [Fact]
    public void TheToolGivesThePathOnAFallbackHarnessAndHoldsNoPromptText()
    {
        string reply = DocsTaskPromptToolTests.Tools(_fixture).BuildTaskPrompt(taskId: "T1", harness: "warp");

        Assert.Contains("prompt file: .agent_scratchpad/t1/prompt.md", reply, StringComparison.Ordinal);
        Assert.Contains("read it first", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<task>", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("SHARED-RULES-MARKER", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolGivesNoPathOnARewriteHarness()
    {
        string reply = DocsTaskPromptToolTests.Tools(_fixture).BuildTaskPrompt(taskId: "T1", harness: "claude");

        Assert.Contains("prompt file: none", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("read it first", reply, StringComparison.Ordinal);
        Assert.False(File.Exists(PromptPath()));
    }

    [Fact]
    public void AFileThatCannotBeWrittenIsARefusalNotAPromptTheConductorDispatches()
    {
        // A file where the task folder must be created makes the write fail on every platform.
        string blocking = Path.Combine(_fixture.Root, ".agent_scratchpad");
        Directory.CreateDirectory(_fixture.Root);
        File.WriteAllText(blocking, "not a folder");

        TaskPromptResult result = Build("warp");

        Assert.False(result.Built);
        Assert.Null(result.PromptFile);
        Assert.Contains(".agent_scratchpad", result.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileIsWrittenUnderTheFolderTheRepositoryIgnores()
    {
        TaskPromptResult result = Build("warp");

        Assert.True(result.Built, result.Refusal);
        Assert.StartsWith($"{TaskPromptService.ScratchFolder}/", result.PromptFile!, StringComparison.Ordinal);

        string gitignore = Corpus.ReadText(".gitignore");
        Assert.Contains($"{TaskPromptService.ScratchFolder}/", gitignore, StringComparison.Ordinal);
    }
}
