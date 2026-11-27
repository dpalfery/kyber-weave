using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders the TypeScript plugin shim that bridges a Bun-hosted harness to the
/// <c>kyber-weave-arbiter hook</c> binary through the task 4.3 plugin envelope.
/// </summary>
/// <remarks>
/// <para>
/// The generator is parameterized by harness token because Kilo takes the same shim
/// (task 16.2): the <c>--harness</c> argv element, the envelope's <c>harness</c>
/// field, and the plugin body vary. The spawned argv, the
/// <c>kyber-arbiter.plugin-event/v1</c> envelope shape
/// (<c>{schema, harness, phase, tool, call-id, session, cwd, args, result}</c>),
/// and the <c>{decision, reason, args}</c> reply contract stay identical.
/// </para>
/// <para>
/// The default export is the shape each harness's loader reads. OpenCode 2.x
/// loads <c>{ id, setup }</c> and calls <c>setup(ctx)</c>; the setup function
/// registers each hook through <c>await ctx.tool.hook("execute.before", callback)</c>
/// and <c>await ctx.tool.hook("execute.after", callback)</c>, and returns a cleanup
/// that disposes both registrations. A 1.x hook object
/// (<c>{"tool.execute.before": ...}</c>) registers nothing under 2.x, so its hook
/// silently never ran. Kilo's fork still loads <c>{ id, server }</c> and reads the
/// 1.x hook object from that function, so Kilo keeps that body byte for byte. Pi
/// is not rendered by this generator (its Arbiter is
/// <c>PiRenderer.RenderArbiterExtension</c>, in the Pi API) and is rejected as a
/// token.
/// </para>
/// <para>
/// The shim spawns by argv with <c>Bun.spawn</c> and no shell, writes the envelope
/// on stdin, ends stdin, and reads stdout as text. A <c>block</c> decision throws
/// <c>Error(reason)</c> under either harness. A returned <c>args</c> object is
/// assigned to the host's writable input: <c>event.input</c> under OpenCode 2.x and
/// <c>output.args</c> under Kilo. A Kilo <c>block</c> on the after hook also appends
/// the reason to <c>output.output</c>. A spawn failure throws, so the call is
/// blocked rather than passed through.
/// </para>
/// <para>
/// The host may run with a cwd outside the repository, so the harness-reported
/// <c>directory</c> is never trusted on its own. At project scope the shim
/// resolves the repository root by walking up from its own plugin file location
/// to the nearest <c>.kyber-weave/kyber-weave.yml</c>, falling back to the nearest
/// <c>.git</c>, then to the same walk from the harness directory. At global scope
/// the plugin file sits under the harness's user-level directory, where the user's
/// own <c>~/.kyber-weave/kyber-weave.yml</c> would otherwise win over the
/// repository: the walk starts from the harness directory only, checks both
/// markers at every level, and returns the nearest directory holding either. The
/// resolved root is bound into both the spawn <c>cwd</c> and the envelope
/// <c>cwd</c>. When no marker resolves, the hook handler throws a block with a
/// clear reason instead of spawning with a possibly-wrong cwd.
/// </para>
/// </remarks>
public static class ArbiterPluginShim
{
    /// <summary>The shape every harness token has: lowercase, digits and hyphens.</summary>
    private const string TokenPatternText = "^[a-z0-9-]+$";

    private static readonly Regex TokenPattern = new(TokenPatternText, RegexOptions.CultureInvariant);

    /// <summary>
    /// Kilo's loader reads the default export as <c>{ id, server }</c> and the server function as the
    /// 1.x hook object, so its imports and body must stay byte-identical to the pre-2.x render.
    /// </summary>
    private const string KiloImports = """
        import type { Plugin } from "@opencode-ai/plugin";
        import { existsSync } from "node:fs";
        import { dirname, join, resolve } from "node:path";
        import { fileURLToPath } from "node:url";
        """;

    /// <summary>
    /// OpenCode 2.x publishes its plugin types in <c>@opencode/plugin</c>, not the 1.x
    /// <c>@opencode-ai/plugin</c> package, so the shim declares the little it needs locally and
    /// depends on no package. The project scope still walks up from the plugin file and needs
    /// <c>fileURLToPath</c>; the global scope does not.
    /// </summary>
    private const string OpenCodeProjectImports = """
        import { existsSync } from "node:fs";
        import { dirname, join, resolve } from "node:path";
        import { fileURLToPath } from "node:url";
        """;

    private const string OpenCodeGlobalImports = """
        import { existsSync } from "node:fs";
        import { dirname, join, resolve } from "node:path";
        """;

    private const string DecisionHelpers = """
        type ArbiterDecision = {
          decision: "allow" | "block";
          reason?: string;
          args?: Record<string, unknown>;
        };

        // JSON.parse accepts null, a bare string, an array and an object with no
        // decision field, and the shim believed whatever came back: a document whose
        // decision is not "block" is read as an allow. Only a plain object whose
        // decision is exactly "allow" or "block" is a decision at all; anything else
        // throws here and the caller turns it into a block.
        function parseDecision(text: string): ArbiterDecision {
          const parsed: unknown = JSON.parse(text);
          if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
            throw new Error("kyber-arbiter: malformed arbiter decision");
          }
          const candidate = parsed as { decision?: unknown; reason?: unknown; args?: unknown };
          if (candidate.decision !== "allow" && candidate.decision !== "block") {
            throw new Error("kyber-arbiter: malformed arbiter decision");
          }
          const decision: ArbiterDecision = { decision: candidate.decision };
          if (typeof candidate.reason === "string") {
            decision.reason = candidate.reason;
          }
          if (typeof candidate.args === "object" && candidate.args !== null && !Array.isArray(candidate.args)) {
            decision.args = candidate.args as Record<string, unknown>;
          }
          return decision;
        }
        """;

    /// <summary>
    /// Project-scope root resolution: the plugin file lives inside the repository, so its own
    /// directory is the most reliable start. Kept verbatim because Kilo's project render is pinned
    /// byte for byte.
    /// </summary>
    private const string ProjectRootResolution = """
        // The host may run with a cwd outside the repository, so the
        // harness-reported directory is never trusted on its own. The repository
        // root is resolved by walking up from this plugin file's own directory to
        // the nearest .kyber-weave/kyber-weave.yml, falling back to the nearest
        // .git, then to the same walk from the harness directory. When no marker
        // resolves, the hook handler throws a block instead of spawning with a
        // possibly-wrong cwd. The resolved root feeds both the spawn cwd and the
        // envelope cwd below.
        function currentFileDirectory(): string {
          const meta = import.meta as { dir?: unknown; url?: unknown };
          if (typeof meta.dir === "string" && meta.dir.length > 0) {
            return meta.dir;
          }
          if (typeof meta.url === "string") {
            try {
              return dirname(fileURLToPath(meta.url));
            } catch {
              // fall through to the host global below
            }
          }
          const globalDirname = (globalThis as { __dirname?: unknown }).__dirname;
          if (typeof globalDirname === "string" && globalDirname.length > 0) {
            return globalDirname;
          }
          return ".";
        }

        function findConfigRoot(startDirectory: string): string | null {
          let current = resolve(startDirectory);
          for (;;) {
            if (existsSync(join(current, ".kyber-weave/kyber-weave.yml"))) {
              return current;
            }
            const parent = dirname(current);
            if (parent === current) {
              return null;
            }
            current = parent;
          }
        }

        function findGitRoot(startDirectory: string): string | null {
          let current = resolve(startDirectory);
          for (;;) {
            if (existsSync(join(current, ".git"))) {
              return current;
            }
            const parent = dirname(current);
            if (parent === current) {
              return null;
            }
            current = parent;
          }
        }

        function resolveRepositoryRoot(harnessDirectory: unknown): string {
          const starts: string[] = [];
          const pluginDirectory = currentFileDirectory();
          if (pluginDirectory.length > 0) {
            starts.push(pluginDirectory);
          }
          if (typeof harnessDirectory === "string" && harnessDirectory.length > 0) {
            starts.push(harnessDirectory);
          }
          for (const start of starts) {
            const configRoot = findConfigRoot(start);
            if (configRoot !== null) {
              return configRoot;
            }
          }
          for (const start of starts) {
            const gitRoot = findGitRoot(start);
            if (gitRoot !== null) {
              return gitRoot;
            }
          }
          throw new Error(
            "kyber-arbiter: cannot resolve repository root from the plugin location or the harness directory " +
            "(looked for .kyber-weave/kyber-weave.yml, falling back to .git)");
        }
        """;

    /// <summary>
    /// Global-scope root resolution: the plugin file lives under the harness's user-level directory,
    /// above which <c>~/.kyber-weave/kyber-weave.yml</c> sits, so the walk starts from the harness
    /// directory only and returns the nearest directory holding either marker.
    /// </summary>
    private const string GlobalRootResolution = """
        // At global scope the plugin file lives under the harness's user-level
        // configuration directory, and the user's ~/.kyber-weave/kyber-weave.yml
        // sits above it: walking up from the plugin file would bind the hook to
        // the home directory rather than to the repository. The root is resolved
        // by walking up from the harness directory only, checking both markers at
        // each level, so the nearest directory holding either
        // .kyber-weave/kyber-weave.yml or .git wins and a repository below the home
        // directory is never beaten by the config at home. When no marker resolves,
        // the hook handler throws a block instead of spawning with a possibly-wrong
        // cwd. The resolved root feeds both the spawn cwd and the envelope cwd below.
        function findNearestRepositoryRoot(startDirectory: string): string | null {
          let current = resolve(startDirectory);
          for (;;) {
            if (
              existsSync(join(current, ".kyber-weave/kyber-weave.yml")) ||
              existsSync(join(current, ".git"))
            ) {
              return current;
            }
            const parent = dirname(current);
            if (parent === current) {
              return null;
            }
            current = parent;
          }
        }

        function resolveRepositoryRoot(harnessDirectory: unknown): string {
          if (typeof harnessDirectory === "string" && harnessDirectory.length > 0) {
            const root = findNearestRepositoryRoot(harnessDirectory);
            if (root !== null) {
              return root;
            }
          }
          throw new Error(
            "kyber-arbiter: cannot resolve repository root from the harness directory " +
            "(looked for .kyber-weave/kyber-weave.yml or .git)");
        }
        """;

    /// <summary>
    /// Kilo's 1.x body: the server function returns the hook object, whose writable argument object
    /// carries <c>output.args</c>. Its bytes must not change.
    /// </summary>
    private static string KiloPluginBody(string harnessLiteral) => $$"""
        export const KyberArbiter: Plugin = async ({ directory }) => ({
          "tool.execute.before": async (input, output) => {
            const root = resolveRepositoryRoot(directory);
            const envelope = {
              schema: "kyber-arbiter.plugin-event/v1",
              harness: {{harnessLiteral}},
              phase: "before",
              tool: input.tool,
              "call-id": input.callID,
              session: input.sessionID,
              cwd: root,
              args: input.args ?? output.args,
            };
            const decision = await runArbiterHook(envelope, root);
            if (decision.decision === "block") {
              throw new Error(decision.reason ?? "blocked by kyber-arbiter");
            }
            if (decision.args !== undefined) {
              output.args = decision.args;
            }
          },
          "tool.execute.after": async (input, output) => {
            const root = resolveRepositoryRoot(directory);
            const envelope = {
              schema: "kyber-arbiter.plugin-event/v1",
              harness: {{harnessLiteral}},
              phase: "after",
              tool: input.tool,
              "call-id": input.callID,
              session: input.sessionID,
              cwd: root,
              args: input.args,
              result: {
                title: output.title,
                output: output.output,
                metadata: output.metadata,
              },
            };
            const decision = await runArbiterHook(envelope, root);
            if (decision.decision === "block") {
              const reason = decision.reason ?? "blocked by kyber-arbiter";
              output.output = (output.output ?? "") + "\n" + reason;
              throw new Error(reason);
            }
          },
        });
        """;

    /// <summary>
    /// OpenCode 2.x's body: setup registers both hooks through <c>ctx.tool.hook</c>, the event's
    /// writable <c>input</c> carries the replaced args, and the cleanup disposes both registrations.
    /// </summary>
    private static string OpenCodePluginBody(string harnessLiteral) => $$"""
        type ArbiterHookContext = {
          location?: { directory?: string };
          tool: {
            hook: (
              name: string,
              callback: (event: any) => Promise<void> | void,
            ) => Promise<{ dispose: () => Promise<void> }>;
          };
        };

        // OpenCode 2.x loads the default export, calls setup(ctx), and reads no hook
        // object from it: a 1.x tool.execute.before hook object registers nothing, so
        // the hook never ran. Each hook is registered through ctx.tool.hook instead, and
        // the returned registrations are disposed by the cleanup setup returns.
        export const KyberArbiter = async (ctx: ArbiterHookContext) => {
          const before = await ctx.tool.hook("execute.before", async (event: any) => {
            const root = resolveRepositoryRoot(ctx.location?.directory);
            const envelope = {
              schema: "kyber-arbiter.plugin-event/v1",
              harness: {{harnessLiteral}},
              phase: "before",
              tool: event.tool,
              "call-id": event.id,
              session: event.sessionID,
              cwd: root,
              args: event.input,
            };
            const decision = await runArbiterHook(envelope, root);
            if (decision.decision === "block") {
              throw new Error(decision.reason ?? "blocked by kyber-arbiter");
            }
            if (decision.args !== undefined) {
              event.input = decision.args;
            }
          });
          const after = await ctx.tool.hook("execute.after", async (event: any) => {
            const root = resolveRepositoryRoot(ctx.location?.directory);
            const envelope = {
              schema: "kyber-arbiter.plugin-event/v1",
              harness: {{harnessLiteral}},
              phase: "after",
              tool: event.tool,
              "call-id": event.id,
              session: event.sessionID,
              cwd: root,
              args: event.input,
              result:
                event.status === "error"
                  ? { status: "error", error: event.error }
                  : { status: "completed", output: event.result?.output, metadata: event.result?.metadata },
            };
            const decision = await runArbiterHook(envelope, root);
            if (decision.decision === "block") {
              throw new Error(decision.reason ?? "blocked by kyber-arbiter");
            }
          });
          return async () => {
            await before.dispose();
            await after.dispose();
          };
        };
        """;

    private const string KiloDefaultExport = """
        export default {
          id: "kyber-arbiter",
          server: KyberArbiter,
        };
        """;

    private const string OpenCodeDefaultExport = """
        export default {
          id: "kyber-arbiter",
          setup: KyberArbiter,
        };
        """;

    /// <summary>Renders the plugin shim for the given harness token (for example <c>opencode</c>).</summary>
    /// <remarks>
    /// The token reaches the generated source twice, so it is both constrained and
    /// encoded. <c>^[a-z0-9-]+$</c> is the shape every harness token already has, and it
    /// excludes the quote that would otherwise close the surrounding TypeScript string
    /// literal. The value is then written as a JSON string, so even a token that slipped
    /// through could not break out of the literal it lands in.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when the token is not a plain lowercase token.</exception>
    public static string Render(string harnessToken, SquadDeploymentScope scope = SquadDeploymentScope.Project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessToken);
        if (!TokenPattern.IsMatch(harnessToken))
        {
            throw new ArgumentException(
                $"Arbiter plugin harness token '{harnessToken}' is not a plain lowercase " +
                $"token: it must match {TokenPatternText}.",
                nameof(harnessToken));
        }

        // The default export and the plugin body are the only things that differ by
        // harness. OpenCode 2.x registers through ctx.tool.hook; Kilo's fork still reads
        // the 1.x hook object from its server function. An unknown harness (including
        // pi, whose Arbiter is a separate Pi-API renderer) has no known loader contract
        // here, so it is rejected rather than guessed at with another harness's shape.
        bool isGlobal = scope == SquadDeploymentScope.Global;

        // JSON string encoding, so the token lands inside the TypeScript literal as data.
        string literal = JsonSerializer.Serialize(harnessToken);

        (string imports, string body, string defaultExport) = harnessToken switch
        {
            HarnessRegistry.OpenCodeToken => (
                isGlobal ? OpenCodeGlobalImports : OpenCodeProjectImports,
                OpenCodePluginBody(literal),
                OpenCodeDefaultExport),
            HarnessRegistry.KiloToken => (KiloImports, KiloPluginBody(literal), KiloDefaultExport),
            _ => throw new ArgumentException(
                $"Arbiter plugin harness token '{harnessToken}' has no known loader " +
                $"default-export shape: expected one of opencode, kilo.",
                nameof(harnessToken)),
        };

        string scopeSuffix = isGlobal ? ", \"--scope\", \"global\"" : string.Empty;
        string commandLine = isGlobal ? ArbiterHookWiring.HookCommandLine(harnessToken, scope) : string.Empty;

        string rootResolution = isGlobal ? GlobalRootResolution : ProjectRootResolution;
        return string.Join(
            "\n\n",
            imports,
            DecisionHelpers,
            rootResolution,
            RunArbiterHook(commandLine, literal, scopeSuffix),
            body) + "\n" + defaultExport;
    }

    /// <summary>
    /// The spawned-hook helper both bodies share. At global scope the generated file also carries the
    /// resolved command line, exactly as before.
    /// </summary>
    private static string RunArbiterHook(string commandLine, string harnessLiteral, string scopeSuffix)
    {
        string commandLineStatement = commandLine.Length == 0
            ? string.Empty
            : $"const commandLine = {JsonSerializer.Serialize(commandLine)};\n";
        return $$"""
            {{commandLineStatement}}async function runArbiterHook(envelope: unknown, cwd: string): Promise<ArbiterDecision> {
              let proc: ReturnType<typeof Bun.spawn>;
              try {
                proc = Bun.spawn(["kyber-weave-arbiter", "hook", "--harness", {{harnessLiteral}}{{scopeSuffix}}], {
                  stdin: "pipe",
                  stdout: "pipe",
                  cwd,
                });
              } catch (err) {
                throw new Error("kyber-arbiter: failed to spawn kyber-weave-arbiter: " + String(err));
              }
              try {
                proc.stdin.write(JSON.stringify(envelope));
                proc.stdin.end();
                const text = await new Response(proc.stdout).text();
                const code = await proc.exited;
                if (code !== 0) {
                  // Exit 0 with empty stdout is the host's deliberate allow. A crash, a kill
                  // or a missing runtime also leaves stdout empty, and must never read as one.
                  throw new Error("kyber-arbiter: hook exited with status " + String(code));
                }
                if (text.trim() === "") {
                  return { decision: "allow" };
                }
                return parseDecision(text);
              } catch (err) {
                throw new Error("kyber-arbiter: failed to read arbiter decision: " + String(err));
              }
            }
            """;
    }
}
