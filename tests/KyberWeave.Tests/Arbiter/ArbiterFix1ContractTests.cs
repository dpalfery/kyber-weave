using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task fix1 (Phase 1 review fixes, council review 10.1):
/// F1 fail-closed stdin/construction faults, F2 macOS key quoting,
/// F3 no hand-written unsafe, F4 dispatcher-plus-guarded overlap.
/// RED: each test fails before its fix.
/// </summary>
public sealed class ArbiterFix1ContractTests : IDisposable
{
    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    // F1: a throwing stdin read must block, never exit-1-with-empty-stdout.
    [Fact]
    public async Task Fix1_ThrowingStdinRead_BlocksFailClosed()
    {
        using ThrowingReader stdin = new();
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", "claude", "--caller", "conductor"], stdin, stdout, stderr);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
        string reason = doc.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
    }

    // F1: a throwing host construction must block too, never exit-1-with-empty-stdout.
    [Fact]
    public async Task Fix1_ThrowingConstruction_BlocksFailClosed()
    {
        using StringReader stdin = new("{}");
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", "claude", "--caller", "conductor"],
            stdin,
            stdout,
            stderr,
            _ => throw new InvalidOperationException("registry exploded"));

        Assert.Equal(0, exit);
        Assert.NotEqual(string.Empty, stdout.ToString());
        Assert.Contains(HookCommand.FailClosedCode, stdout.ToString(), StringComparison.Ordinal);
    }

    // F2: keys with space, quote and backslash must round-trip through the
    // `security -i` stdin command, never on argv; newline must be rejected.
    [Theory]
    [InlineData("key with space")]
    [InlineData("key\"with\"quotes")]
    [InlineData("key\\with\\backslash")]
    public void Fix2_MacWrite_SpecialKeysStayQuotedOnStdinOnly(string key)
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, string.Empty, string.Empty);
        MacKeychainCredentialStore store = new(runner);

        store.Write("https://api.typesafe.ai", key);

        CapturedCall call = Assert.Single(runner.Calls);
        Assert.Equal(["-i"], call.Argv);
        Assert.DoesNotContain(key, string.Join(" ", call.Argv), StringComparison.Ordinal);
        // The key must be quoted so `security -i` parses it as one argument.
        Assert.Contains($"\"{key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"", call.StandardInput, StringComparison.Ordinal);
    }

    [Fact]
    public void Fix2_MacWrite_NewlineKey_RejectedWithClearError()
    {
        FakeCredentialProcessRunner runner = new();
        MacKeychainCredentialStore store = new(runner);

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => store.Write("https://api.typesafe.ai", "abc\ndef"));

        Assert.DoesNotContain("abc\ndef", failure.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    // F3: no hand-written `unsafe` in Core outside generated LibraryImport stubs.
    [Fact]
    public void Fix3_CoreHasNoHandWrittenUnsafe()
    {
        string coreRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, "src", "KyberWeave.Core");
        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
        {
            foreach (string line in StripCommentsAndStrings(File.ReadAllText(file)).Split('\n'))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\bunsafe\b"))
                {
                    offenders.Add($"{file}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Hand-written unsafe outside generated stubs:\n" + string.Join("\n", offenders));
    }

    // F4: an agent that is both dispatcher and guarded keeps BOTH hooks.
    [Fact]
    public async Task Fix4_DispatcherPlusGuardedAgent_KeepsBothHooks()
    {
        // Make csharp-dev (worker profile, dispatch target) a dispatcher too.
        string agentPath = Path.Combine(_fixture.Path, "agents", "csharp-dev.md");
        await File.WriteAllTextAsync(agentPath, """
            ---
            schema: kyber-squad.agent/v1
            name: csharp-dev
            description: Arbiter fixture agent exercising the worker profile.
            invocation: subagent
            model-profile: general
            capability-profile: worker
            copilot-tools: [vscode, read]
            delegates-to: [research-agent]
            fallback: role-skill
            aliases: []
            ---
            Follow the worker instruction body.
            """);

        ClaudeRenderer renderer = new();
        SquadRenderResult result = await renderer.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadDeploymentFile file = Assert.Single(
            result.Files, f => f.RelativePath == ".claude/agents/csharp-dev.md");
        YamlMappingNode frontmatter = SplitFrontmatter(Encoding.UTF8.GetString(file.Content.Span));
        YamlMappingNode hooks = RequireMapping(frontmatter, "hooks");
        YamlSequenceNode pre = RequireSequence(hooks, "PreToolUse");

        string[] matchers = pre.Children.OfType<YamlMappingNode>()
            .Select(MatcherOf).ToArray();
        Assert.Contains("^(Agent|Task)$", matchers);
        Assert.Contains("^(Read|Grep|Glob|Bash)$", matchers);
        Assert.Contains("^SubagentHandback$", matchers);
    }

    private sealed class ThrowingReader : TextReader
    {
        public override Task<string> ReadToEndAsync() =>
            throw new InvalidOperationException("stdin exploded");
    }

    private sealed record CapturedCall(string FileName, string[] Argv, string StandardInput);

    private sealed class FakeCredentialProcessRunner : ICredentialProcessRunner
    {
        public List<CapturedCall> Calls { get; } = [];

        public ProcessResult NextResult { get; set; } = new(0, string.Empty, string.Empty);

        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
        {
            string[] argv = [.. startInfo.ArgumentList];
            Calls.Add(new CapturedCall(startInfo.FileName, argv, standardInput));
            return NextResult;
        }
    }

    private static string StripCommentsAndStrings(string text)
    {
        StringBuilder kept = new(text.Length);
        int index = 0;
        bool inLineComment = false;
        bool inBlockComment = false;
        bool inString = false;
        bool inChar = false;
        bool verbatim = false;
        while (index < text.Length)
        {
            char current = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';
            if (inLineComment)
            {
                if (current == '\n')
                {
                    inLineComment = false;
                    kept.Append('\n');
                }

                index++;
                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index += 2;
                }
                else
                {
                    if (current == '\n')
                    {
                        kept.Append('\n');
                    }

                    index++;
                }

                continue;
            }

            if (inString)
            {
                if (!verbatim && current == '\\')
                {
                    index += 2;
                    continue;
                }

                if (current == '"')
                {
                    if (verbatim && next == '"')
                    {
                        index += 2;
                        continue;
                    }

                    inString = false;
                }

                index++;
                continue;
            }

            if (inChar)
            {
                if (current == '\\')
                {
                    index += 2;
                    continue;
                }

                if (current == '\'')
                {
                    inChar = false;
                }

                index++;
                continue;
            }

            if (current == '/' && next == '/')
            {
                inLineComment = true;
                index += 2;
                continue;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index += 2;
                continue;
            }

            if (current == '@' && next == '"')
            {
                inString = true;
                verbatim = true;
                index += 2;
                continue;
            }

            if (current == '$' && next == '"')
            {
                inString = true;
                verbatim = false;
                index += 2;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                verbatim = false;
                index++;
                continue;
            }

            if (current == '\'')
            {
                inChar = true;
                index++;
                continue;
            }

            kept.Append(current);
            index++;
        }

        return kept.ToString();
    }

    private static YamlMappingNode SplitFrontmatter(string text)
    {
        const string delimiter = "---\n";
        Assert.True(text.StartsWith(delimiter, StringComparison.Ordinal), "Missing frontmatter delimiter.");
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, "Missing closing frontmatter delimiter.");
        string yaml = text[delimiter.Length..(end + 1)];
        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static YamlMappingNode RequireMapping(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            throw new InvalidOperationException($"Frontmatter is missing required key '{key}'.");
        }

        return Assert.IsType<YamlMappingNode>(value);
    }

    private static YamlSequenceNode RequireSequence(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            throw new InvalidOperationException($"Frontmatter is missing required key '{key}'.");
        }

        return Assert.IsType<YamlSequenceNode>(value);
    }

    private static string MatcherOf(YamlMappingNode entry)
    {
        if (!entry.Children.TryGetValue(new YamlScalarNode("matcher"), out YamlNode? value))
        {
            return string.Empty;
        }

        return Assert.IsType<YamlScalarNode>(value).Value ?? string.Empty;
    }
}
