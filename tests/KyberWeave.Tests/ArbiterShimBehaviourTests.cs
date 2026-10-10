using System.Diagnostics;
using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Runs the generated OpenCode shim and Pi extension under Bun against a fake
/// <c>kyber-weave-arbiter</c> binary, because asserting on the generated TypeScript text
/// cannot tell a hook that allows on purpose from one that allows because it crashed.
/// The host's only allow is exit 0 with empty stdout (or a <c>decision: allow</c>
/// document); every other outcome has to block. Skipped when Bun or a POSIX shell is
/// missing.
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
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OpenCodeShim_FailsClosedUnlessTheHostAllowsOnPurpose(string stdout, string exit, string expected)
    {
        string bun = RequireBun();
        WriteFakeBinary(stdout, exit);
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "shim.ts"), ArbiterPluginShim.Render("opencode"));
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

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task KiloShim_FailsClosedUnlessTheHostAllowsOnPurpose(string stdout, string exit, string expected)
    {
        // Review 20.1, f: the Kilo shim is the same generator under the 'kilo' token, and
        // it must hold the same fail-closed table as the OpenCode shim under Bun.
        string bun = RequireBun();
        WriteFakeBinary(stdout, exit);
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

        string output = await RunBunAsync(bun, "drive.ts");

        Assert.StartsWith(Blocked, output, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------

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
        SquadRendererRegistry registry = new([new KiloRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Kilo],
            Scope: SquadDeploymentScope.Project,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadDeploymentFile shim = Assert.Single(
            result.Files, f => f.RelativePath == ".kilo/plugin/kyber-arbiter.ts");
        return Encoding.UTF8.GetString(shim.Content.Span);
    }

    private async Task<string> RenderPiExtensionAsync(int timeoutSeconds)
    {
        SquadRendererRegistry registry = new([new PiRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Pi],
            Scope: SquadDeploymentScope.Project,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: timeoutSeconds)));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadDeploymentFile extension = Assert.Single(
            result.Files, f => f.RelativePath == ".pi/extensions/kyber-arbiter.ts");
        return Encoding.UTF8.GetString(extension.Content.Span);
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

    private async Task<string> RunBunAsync(string bun, string script)
    {
        ProcessStartInfo startInfo = new(bun)
        {
            WorkingDirectory = _temp.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(script);
        // The fake binary shadows any real kyber-weave-arbiter the host may have installed.
        startInfo.Environment["PATH"] = _temp.Path + Path.PathSeparator
            + Environment.GetEnvironmentVariable("PATH");

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
