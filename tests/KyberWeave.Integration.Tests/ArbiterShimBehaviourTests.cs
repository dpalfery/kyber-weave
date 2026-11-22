using System.Diagnostics;
using KyberWeave.Tests.Fakes;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Runs the generated OpenCode and Kilo plugin shims and the Pi extension under Bun against a
/// fake <c>kyber-weave-arbiter</c> binary, because asserting on the generated TypeScript text
/// cannot tell a hook that allows on purpose from one that allows because it crashed. OpenCode
/// 2.x is driven through the default export's <c>setup</c> and its <c>ctx.tool.hook</c>
/// registrations; Kilo stays on the 1.x <c>{ id, server }</c> hook object. The host's only allow
/// is exit 0 with empty stdout (or a <c>decision: allow</c> document); every other outcome has
/// to block. Skipped when Bun or a POSIX shell is missing.
/// </summary>
public sealed class ArbiterShimBehaviourTests : IDisposable
{
    private const string Blocked = "BLOCKED";
    private const string Allowed = "ALLOWED";

    private readonly TempDirectory _temp = new();
    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose()
    {
        _temp.Dispose();
        _fixture.Dispose();
    }

    public static TheoryData<string, string, string> Cases => new()
    {
        // stdout, exit code, expected
        { string.Empty, "0", Allowed },
        { "{\"decision\":\"allow\"}", "0", Allowed },
        { "{\"decision\":\"block\",\"reason\":\"nope\"}", "0", Blocked },
        { string.Empty, "150", Blocked },
        { string.Empty, "137", Blocked },
        { string.Empty, "2", Blocked },
        { "{\"decision\":\"allow\"}", "1", Blocked },
        { "this is not json", "0", Blocked },
        // Well-formed JSON that is not a decision. `JSON.parse` accepts all of these
        // and the shim believed whatever came back: a non-block decision is an allow,
        // so a document the host never meant as an allow was read as one.
        { "null", "0", Blocked },
        { "\"x\"", "0", Blocked },
        { "[]", "0", Blocked },
        { "{}", "0", Blocked },
        { "{\"decision\":\"maybe\"}", "0", Blocked },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OpenCodeShim_FailsClosedUnlessTheHostAllowsOnPurpose(string stdout, string exit, string expected)
    {
        string bun = RequireBun();
        WriteFakeBinary(stdout, exit);
        PlantRepositoryMarker();
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "shim.ts"), ArbiterPluginShim.Render("opencode"));
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "drive.ts"), """
            import plugin from "./shim.ts";
            const hooks: any = {};
            const ctx = {
              location: { directory: process.cwd() },
              tool: {
                hook: async (name: string, callback: any) => {
                  hooks[name] = callback;
                  return { dispose: async () => {} };
                },
              },
            };
            await plugin.setup(ctx);
            try {
              const event: any = {
                tool: "task", id: "c1", sessionID: "s1", agent: "build", messageID: "m1",
                input: { subagent_type: "x", prompt: "p" },
              };
              await hooks["execute.before"](event);
              console.log("ALLOWED");
            } catch (e) {
              console.log("BLOCKED:" + String(e));
            }
            """);

        string output = await RunBunAsync(bun, "drive.ts");

        Assert.StartsWith(expected, output, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task KiloShim_FailsClosedUnlessTheHostAllowsOnPurpose(string stdout, string exit, string expected)
    {
        // Review 20.1, f: the Kilo shim is the same generator under the 'kilo' token, and
        // it must hold the same fail-closed table as the OpenCode shim under Bun.
        string bun = RequireBun();
        WriteFakeBinary(stdout, exit);
        PlantRepositoryMarker();
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "shim.ts"), await RenderKiloShimAsync());
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "drive.ts"), """
            import { KyberArbiter } from "./shim.ts";
            const hooks: any = await KyberArbiter({ directory: process.cwd() } as any);
            try {
              await hooks["tool.execute.before"](
                { tool: "task", callID: "c1", sessionID: "s1", args: { subagent_type: "x", prompt: "p" } },
                { args: {} });
              console.log("ALLOWED");
            } catch (e) {
              console.log("BLOCKED:" + String(e));
            }
            """);

        string output = await RunBunAsync(bun, "drive.ts");

        Assert.StartsWith(expected, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenCodeShim_SetupRegistersBothHooksAndMapsTheEvent()
    {
        // OpenCode 2.x reads only the default export { id, setup } and registers hooks
        // through ctx.tool.hook. Loading through the named export cannot see that
        // contract, so this drives setup exactly as the loader does: both hooks are
        // registered, the arbiter's replacement args land on the event's writable
        // `input`, call id and session reach the envelope, both after statuses map onto
        // `result`, and the cleanup disposes both registrations.
        string bun = RequireBun();
        WriteCapturingBinary("{\"decision\":\"allow\",\"args\":{\"prompt\":\"replaced\"}}");
        PlantRepositoryMarker();
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "shim.ts"), ArbiterPluginShim.Render("opencode"));
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "drive.ts"), """
            import plugin from "./shim.ts";
            const hooks: any = {};
            const disposed: string[] = [];
            const ctx = {
              location: { directory: process.cwd() },
              tool: {
                hook: async (name: string, callback: any) => {
                  hooks[name] = callback;
                  return { dispose: async () => { disposed.push(name); } };
                },
              },
            };
            const cleanup = await plugin.setup(ctx);
            const before: any = {
              tool: "subagent", id: "call-7", sessionID: "session-9", agent: "build",
              messageID: "m1", input: { prompt: "original" },
            };
            await hooks["execute.before"](before);
            console.log("inputPrompt=" + before.input.prompt);
            await hooks["execute.after"]({
              tool: "subagent", id: "call-7", sessionID: "session-9", agent: "build", messageID: "m1",
              input: before.input, status: "completed", result: { output: "done", metadata: { m: 1 } },
            });
            await hooks["execute.after"]({
              tool: "subagent", id: "call-7", sessionID: "session-9", agent: "build", messageID: "m1",
              input: before.input, status: "error", error: "boom",
            });
            await cleanup();
            console.log("disposed=" + disposed.join(","));
            """);

        string output = await RunBunAsync(bun, "drive.ts");

        Assert.Contains("inputPrompt=replaced", output, StringComparison.Ordinal);
        Assert.Contains("disposed=execute.before,execute.after", output, StringComparison.Ordinal);

        string[] envelopes = (await File.ReadAllLinesAsync(Path.Combine(_temp.Path, "envelopes.log")))
            .Where(line => line.Trim().Length > 0)
            .ToArray();
        Assert.Equal(3, envelopes.Length);

        JsonElement beforeEvent = JsonDocument.Parse(envelopes[0]).RootElement;
        Assert.Equal("kyber-arbiter.plugin-event/v1", beforeEvent.GetProperty("schema").GetString());
        Assert.Equal("opencode", beforeEvent.GetProperty("harness").GetString());
        Assert.Equal("before", beforeEvent.GetProperty("phase").GetString());
        Assert.Equal("subagent", beforeEvent.GetProperty("tool").GetString());
        Assert.Equal("call-7", beforeEvent.GetProperty("call-id").GetString());
        Assert.Equal("session-9", beforeEvent.GetProperty("session").GetString());
        Assert.False(string.IsNullOrWhiteSpace(beforeEvent.GetProperty("cwd").GetString()));
        Assert.Equal("original", beforeEvent.GetProperty("args").GetProperty("prompt").GetString());

        JsonElement completed = JsonDocument.Parse(envelopes[1]).RootElement.GetProperty("result");
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal("done", completed.GetProperty("output").GetString());
        Assert.Equal(1, completed.GetProperty("metadata").GetProperty("m").GetInt32());

        JsonElement errored = JsonDocument.Parse(envelopes[2]).RootElement.GetProperty("result");
        Assert.Equal("error", errored.GetProperty("status").GetString());
        Assert.Equal("boom", errored.GetProperty("error").GetString());
    }

    [Fact]
    public async Task OpenCodeShim_GlobalScope_ResolvesTheRepositoryRootFromTheHarnessDirectory()
    {
        // A globally installed plugin file sits under the harness's user-level directory,
        // and a user machine also carries ~/.kyber-weave/kyber-weave.yml. Walking up from
        // the plugin file would bind the hook to home; at global scope the walk starts
        // from the harness directory instead, so the repository below home wins.
        string bun = RequireBun();
        WriteCapturingBinary("{\"decision\":\"allow\"}");

        string home = Path.Combine(_temp.Path, "home");
        string repository = Path.Combine(home, "repo");
        string plugins = Path.Combine(home, ".config", "opencode", "plugins");
        Directory.CreateDirectory(Path.Combine(home, ".kyber-weave"));
        await File.WriteAllTextAsync(Path.Combine(home, ".kyber-weave", "kyber-weave.yml"), "ontology:\n");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(plugins);

        await File.WriteAllTextAsync(
            Path.Combine(plugins, "shim.ts"),
            ArbiterPluginShim.Render("opencode", SquadDeploymentScope.Global));
        await File.WriteAllTextAsync(Path.Combine(repository, "drive.ts"), """
            import plugin from "../.config/opencode/plugins/shim.ts";
            const hooks: any = {};
            const ctx = {
              location: { directory: process.cwd() },
              tool: {
                hook: async (name: string, callback: any) => {
                  hooks[name] = callback;
                  return { dispose: async () => {} };
                },
              },
            };
            await plugin.setup(ctx);
            await hooks["execute.before"]({
              tool: "task", id: "c1", sessionID: "s1", agent: "build", messageID: "m1", input: {},
            });
            console.log("cwd=" + process.cwd());
            """);

        string output = await RunBunAsync(bun, "drive.ts", workingDirectory: repository);

        // The capture binary writes its stdin relative to its own cwd, so the file lands
        // in the repository only when the spawn cwd is the repository, not home.
        string envelopePath = Path.Combine(repository, "envelopes.log");
        Assert.True(
            File.Exists(envelopePath),
            "the arbiter hook must spawn with the repository as its cwd, not the home directory");
        Assert.False(File.Exists(Path.Combine(home, "envelopes.log")));

        string driverCwd = output.Split('\n')
            .First(line => line.StartsWith("cwd=", StringComparison.Ordinal))["cwd=".Length..]
            .Trim();
        string envelopeCwd = JsonDocument.Parse(await File.ReadAllTextAsync(envelopePath))
            .RootElement.GetProperty("cwd").GetString()!;
        Assert.Equal(driverCwd, envelopeCwd);
        Assert.True(
            envelopeCwd.EndsWith(Path.Combine("home", "repo"), StringComparison.Ordinal),
            $"the envelope cwd must be the repository below home, but was '{envelopeCwd}'");
    }

    [Theory]
    [InlineData("{\"decision\":\"allow\"}", Allowed)]
    [InlineData("{\"decision\":\"block\",\"reason\":\"nope\"}", Blocked)]
    public async Task KiloShim_DefaultExportLoadsThroughServerAndPinsBothDecisions(string stdout, string expected)
    {
        // The Kilo loader reads the default export { id, server }. Loading
        // through the named export cannot see a missing or wrong-shape default
        // export, so this test mirrors the loader contract like the OpenCode
        // case above. Both decisions are pinned through that path: a server
        // that always blocks would otherwise still satisfy a block-only test.
        string bun = RequireBun();
        WriteFakeBinary(stdout, "0");
        PlantRepositoryMarker();
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "shim.ts"), ArbiterPluginShim.Render("kilo"));
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "drive.ts"), """
            import plugin from "./shim.ts";
            const hooks: any = await plugin.server({ directory: process.cwd() } as any);
            try {
              await hooks["tool.execute.before"](
                { tool: "task", callID: "c1", sessionID: "s1", args: { subagent_type: "x", prompt: "p" } },
                { args: {} });
              console.log("ALLOWED");
            } catch (e) {
              console.log("BLOCKED:" + String(e));
            }
            """);

        string output = await RunBunAsync(bun, "drive.ts");

        Assert.StartsWith(expected, output, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PiExtension_FailsClosedUnlessTheHostAllowsOnPurpose(string stdout, string exit, string expected)
    {
        string bun = RequireBun();
        WriteFakeBinary(stdout, exit);
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "ext.ts"), await RenderPiExtensionAsync(timeoutSeconds: 5));
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "drive.ts"), PiDriver);

        string output = await RunBunAsync(bun, "drive.ts");

        Assert.StartsWith(expected, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PiExtension_BlocksWhenTheHostOutlivesItsTimeout()
    {
        string bun = RequireBun();
        string script = Path.Combine(_temp.Path, "kyber-weave-arbiter");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 20\n");
        MakeExecutable(script);
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "ext.ts"), await RenderPiExtensionAsync(timeoutSeconds: 1));
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "drive.ts"), PiDriver);

        string output = await RunBunAsync(bun, "drive.ts");

        Assert.StartsWith(Blocked, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PiExtension_BlocksWhenTheHostBinaryIsMissing()
    {
        string bun = RequireBun();
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "ext.ts"), await RenderPiExtensionAsync(timeoutSeconds: 5));
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "drive.ts"), PiDriver);

        // Only bun's own directory and the system directories stay on PATH, so a real
        // kyber-weave-arbiter installed on the machine cannot answer in place of the missing one.
        string output = await RunBunAsync(bun, "drive.ts", inheritPath: false);

        Assert.StartsWith(Blocked, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PiExtension_IgnoresPrototypePollutingKeysInADecisionArgument()
    {
        // A decision carrying `__proto__` is copied field by field onto the host's
        // tool input, and a bracket assignment on `__proto__` runs the inherited
        // setter rather than creating an own key. The host's input object adopted the
        // decision's object as its prototype and inherited its properties from it.
        // These keys are skipped instead. Object.prototype itself is never the write
        // target either way; the assertion pins that too.
        string bun = RequireBun();
        WriteFakeBinary(
            "{\"decision\":\"allow\",\"args\":{\"__proto__\":{\"polluted\":true},\"constructor\":{\"polluted\":true}," +
            "\"prototype\":{\"polluted\":true},\"subagent_type\":\"kept\"}}",
            "0");
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "ext.ts"), await RenderPiExtensionAsync(timeoutSeconds: 5));
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "drive.ts"), """
            import ext from "./ext.ts";
            const handlers: Record<string, (event: any) => Promise<any>> = {};
            (ext as any)({ on: (name: string, handler: (event: any) => Promise<any>) => { handlers[name] = handler; } });
            const input: any = { subagent_type: "x", prompt: "p" };
            await handlers["tool_call"]({ toolName: "Agent", toolCallId: "c1", input });
            console.log("globalPolluted=" + String(({} as any).polluted));
            console.log("inputPolluted=" + String(input.polluted));
            console.log("prototypeSwapped=" + String(Object.getPrototypeOf(input) !== Object.prototype));
            console.log("ownConstructor=" + String(Object.prototype.hasOwnProperty.call(input, "constructor")));
            console.log("ownPrototype=" + String(Object.prototype.hasOwnProperty.call(input, "prototype")));
            console.log("safeKeyApplied=" + String(input.subagent_type));
            """);

        string output = await RunBunAsync(bun, "drive.ts");

        Assert.Contains("globalPolluted=undefined", output, StringComparison.Ordinal);
        Assert.Contains("inputPolluted=undefined", output, StringComparison.Ordinal);
        Assert.Contains("prototypeSwapped=false", output, StringComparison.Ordinal);
        Assert.Contains("ownConstructor=false", output, StringComparison.Ordinal);
        Assert.Contains("ownPrototype=false", output, StringComparison.Ordinal);
        Assert.Contains("safeKeyApplied=kept", output, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------

    [Fact]
    public async Task PiExtension_AfterABlockOnACallWithNoContent_StillBlocks()
    {
        // A call that produced no content carries `content: undefined`, and spreading
        // that threw a TypeError out of the handler. Failing to build the block is not
        // a block: the host saw a crashed hook rather than a refusal.
        string bun = RequireBun();
        WriteFakeBinary("{\"decision\":\"block\",\"reason\":\"nope\"}", "0");
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "ext.ts"), await RenderPiExtensionAsync(timeoutSeconds: 5));
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "drive.ts"), """
            import ext from "./ext.ts";
            const handlers: Record<string, (event: any) => Promise<any>> = {};
            (ext as any)({ on: (name: string, handler: (event: any) => Promise<any>) => { handlers[name] = handler; } });
            const event: any = { toolName: "Agent", toolCallId: "c1", input: {}, isError: false };
            console.log("contentPresent=" + String("content" in event));
            try {
              const result = await handlers["tool_result"](event);
              console.log("RESULT=" + JSON.stringify(result));
            } catch (e) {
              console.log("THREW=" + String(e));
            }
            """);

        string output = await RunBunAsync(bun, "drive.ts");

        Assert.Contains("contentPresent=false", output, StringComparison.Ordinal);
        Assert.DoesNotContain("THREW=", output, StringComparison.Ordinal);
        Assert.Contains("RESULT={\"content\":[{\"type\":\"text\",\"text\":\"nope\"}]}", output, StringComparison.Ordinal);
    }

    [Fact]
    public void KiloShim_ResolvesRepositoryRootFromPluginLocation()
    {
        // The host may run with a cwd outside the repository, so the shim cannot
        // trust the harness-reported directory. It must walk up from its own
        // location to the nearest `.kyber-weave/kyber-weave.yml`, falling back to
        // `.git`, and use that root for everything that follows.
        string content = ArbiterPluginShim.Render("kilo");

        Assert.Contains(".kyber-weave/kyber-weave.yml", content, StringComparison.Ordinal);
        Assert.Contains(".git", content, StringComparison.Ordinal);
        Assert.True(
            content.Contains("import.meta", StringComparison.Ordinal) ||
            content.Contains("__dirname", StringComparison.Ordinal) ||
            content.Contains("dirname", StringComparison.Ordinal),
            "shim must resolve the repository root from its own plugin location, not the harness cwd");

        int configMarker = content.IndexOf(".kyber-weave/kyber-weave.yml", StringComparison.Ordinal);
        int gitMarker = content.IndexOf(".git", StringComparison.Ordinal);
        Assert.True(
            configMarker >= 0 && gitMarker > configMarker,
            "shim must prefer .kyber-weave/kyber-weave.yml and fall back to .git");
    }

    [Fact]
    public void KiloShim_BindsSpawnAndEnvelopeCwdToResolvedRoot()
    {
        // Both the spawned hook process's cwd and the envelope's cwd must be the
        // resolved repository root. Passing the raw harness `directory` through
        // lets a host whose cwd is not the repo make the hook fail closed with
        // KW-ARB-HOOK-001.
        string content = ArbiterPluginShim.Render("kilo");

        Assert.Contains("cwd:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("cwd: directory", content, StringComparison.Ordinal);
        Assert.Contains("runArbiterHook(envelope,", content, StringComparison.Ordinal);
        Assert.DoesNotContain("runArbiterHook(envelope, directory)", content, StringComparison.Ordinal);
    }

    private const string PiDriver = """
        import ext from "./ext.ts";
        const handlers: Record<string, (event: any) => Promise<any>> = {};
        (ext as any)({ on: (name: string, handler: (event: any) => Promise<any>) => { handlers[name] = handler; } });
        const result = await handlers["tool_call"]({
          toolName: "Agent", toolCallId: "c1", input: { subagent_type: "x", prompt: "p" } });
        console.log(result && result.block ? "BLOCKED:" + result.reason : "ALLOWED");
        """;

    private async Task<string> RenderKiloShimAsync()
    {
        SquadRendererRegistry registry = SquadTestRegistry.Create([new KiloRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Kilo],
            Scope: SquadDeploymentScope.Project,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)).Prepared());
        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadDeploymentFile shim = Assert.Single(
            result.Files, f => f.RelativePath == ".kilo/plugin/kyber-arbiter.ts");
        return Encoding.UTF8.GetString(shim.Content.Span);
    }

    private async Task<string> RenderPiExtensionAsync(int timeoutSeconds)
    {
        SquadRendererRegistry registry = SquadTestRegistry.Create([new PiRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Pi],
            Scope: SquadDeploymentScope.Project,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: timeoutSeconds)).Prepared());
        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadDeploymentFile extension = Assert.Single(
            result.Files, f => f.RelativePath == ".pi/extensions/kyber-arbiter.ts");
        return Encoding.UTF8.GetString(extension.Content.Span);
    }

    private void PlantRepositoryMarker()
    {
        // The shim resolves the repository root by walking up from its own
        // location to the nearest `.kyber-weave/kyber-weave.yml` (falling back
        // to `.git`) and blocks when no marker resolves. The temp directory is
        // markerless by default, so without this the shim blocks before the
        // fake binary ever runs and the deliberate-allow rows can never pass.
        string configDirectory = Path.Combine(_temp.Path, ".kyber-weave");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, "kyber-weave.yml"), "ontology:\n");
    }

    private void WriteFakeBinary(string stdout, string exit)
    {
        string script = Path.Combine(_temp.Path, "kyber-weave-arbiter");
        string body = "#!/bin/sh\ncat >/dev/null\n"
            + (stdout.Length > 0 ? $"printf '%s' '{stdout}'\n" : string.Empty)
            + $"exit {exit}\n";
        File.WriteAllText(script, body);
        MakeExecutable(script);
    }

    /// <summary>
    /// A fake binary that appends the envelope it received on stdin to <c>envelopes.log</c> in its
    /// own cwd, then answers with <paramref name="stdout"/>. The log's location proves which
    /// directory the shim spawned the hook in.
    /// </summary>
    private void WriteCapturingBinary(string stdout)
    {
        string script = Path.Combine(_temp.Path, "kyber-weave-arbiter");
        string body = "#!/bin/sh\ncat >> envelopes.log\nprintf '\\n' >> envelopes.log\n"
            + $"printf '%s' '{stdout}'\n"
            + "exit 0\n";
        File.WriteAllText(script, body);
        MakeExecutable(script);
    }

    private async Task<string> RunBunAsync(string bun, string script, bool inheritPath = true, string? workingDirectory = null)
    {
        ProcessStartInfo startInfo = new(bun)
        {
            WorkingDirectory = workingDirectory ?? _temp.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(script);
        // The fake binary shadows any real kyber-weave-arbiter the host may have installed.
        startInfo.Environment["PATH"] = _temp.Path + Path.PathSeparator
            + (inheritPath
                ? Environment.GetEnvironmentVariable("PATH")
                : string.Join(Path.PathSeparator, Path.GetDirectoryName(bun), "/usr/bin", "/bin"));

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start bun.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        string error = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(output.Length > 0, $"bun printed nothing. stderr: {error}");
        return output;
    }

    private static string RequireBun()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake arbiter binary is a POSIX shell script.");
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(directory, "bun");
            if (directory.Length > 0 && File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Skip("The 'bun' runtime is not available, so the generated shims are not executed.");
        return string.Empty;
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
