using System.Text.Json;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders the TypeScript plugin shim that bridges a Bun-hosted harness to the
/// <c>kyber-weave-arbiter hook</c> binary through the task 4.3 plugin envelope.
/// </summary>
/// <remarks>
/// <para>
/// The generator is parameterized by harness token because Kilo takes the same shim
/// (task 16.2): the <c>--harness</c> argv element, the envelope's <c>harness</c>
/// field, and the default-export shape vary. The spawned argv, the
/// <c>kyber-arbiter.plugin-event/v1</c> envelope shape
/// (<c>{schema, harness, phase, tool, call-id, session, cwd, args, result}</c>),
/// and the <c>{decision, reason, args}</c> reply contract stay identical.
/// </para>
/// <para>
/// The default export is the shape each harness's loader reads: OpenCode v2
/// reads only the default export and accepts <c>{ id, setup }</c>; Kilo's fork
/// reads <c>{ id, server }</c>. Pi is not rendered by this generator (its
/// Arbiter is <c>PiRenderer.RenderArbiterExtension</c>, in the Pi API) and is
/// rejected as a token. Emitting only the named <c>KyberArbiter</c> export
/// left the plugin unloaded under OpenCode v2 (fail-open) because its loader
/// never saw a default export.
/// </para>
/// <para>
/// The shim spawns by argv with <c>Bun.spawn</c> and no shell, writes the envelope
/// on stdin, ends stdin, and reads stdout as text. A <c>block</c> decision throws
/// <c>Error(reason)</c>; a returned <c>args</c> object is assigned to
/// <c>output.args</c> (before) and a block reason is appended to
/// <c>output.output</c> (after). A spawn failure throws, so the call is blocked
/// rather than passed through.
/// </para>
/// <para>
/// The host may run with a cwd outside the repository, so the harness-reported
/// <c>directory</c> is never trusted on its own: the shim resolves the repository
/// root by walking up from its own plugin file location to the nearest
/// <c>.kyber-weave/kyber-weave.yml</c> (falling back to the nearest <c>.git</c>,
/// then to the same walk from the harness directory) and binds that root into
/// both the spawn <c>cwd</c> and the envelope <c>cwd</c>. When no marker resolves,
/// the hook handler throws a block with a clear reason instead of spawning with
/// a possibly-wrong cwd.
/// </para>
/// </remarks>
public static class ArbiterPluginShim
{
    /// <summary>The shape every harness token has: lowercase, digits and hyphens.</summary>
    private const string TokenPatternText = "^[a-z0-9-]+$";

    private static readonly Regex TokenPattern = new(TokenPatternText, RegexOptions.CultureInvariant);
    /// <summary>Renders the plugin shim for the given harness token (for example <c>opencode</c>).</summary>
    /// <remarks>
    /// The token reaches the generated source twice, so it is both constrained and
    /// encoded. <c>^[a-z0-9-]+$</c> is the shape every harness token already has, and it
    /// excludes the quote that would otherwise close the surrounding TypeScript string
    /// literal. The value is then written as a JSON string, so even a token that slipped
    /// through could not break out of the literal it lands in.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when the token is not a plain lowercase token.</exception>
    public static string Render(string harnessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessToken);
        if (!TokenPattern.IsMatch(harnessToken))
        {
            throw new ArgumentException(
                $"Arbiter plugin harness token '{harnessToken}' is not a plain lowercase " +
                $"token: it must match {TokenPatternText}.",
                nameof(harnessToken));
        }

        // The default export is the only thing each harness loader reads, and the
        // two loaders disagree on its shape: OpenCode v2 wants { id, setup },
        // Kilo's fork wants { id, server }. An unknown harness (including pi,
        // whose Arbiter is a separate Pi-API renderer) has no known loader
        // contract here, so it is rejected rather than guessed at with another
        // harness's shape.
        string defaultExport = harnessToken switch
        {
            "opencode" => "export default {\n  id: \"kyber-arbiter\",\n  setup: KyberArbiter,\n};",
            "kilo" => "export default {\n  id: \"kyber-arbiter\",\n  server: KyberArbiter,\n};",
            _ => throw new ArgumentException(
                $"Arbiter plugin harness token '{harnessToken}' has no known loader " +
                $"default-export shape: expected one of opencode, kilo.",
                nameof(harnessToken)),
        };

        // JSON string encoding, so the token lands inside the TypeScript literal as data.
        string literal = JsonSerializer.Serialize(harnessToken);
        string template = $$"""
            import type { Plugin } from "@opencode-ai/plugin";
            import { existsSync } from "node:fs";
            import { dirname, join, resolve } from "node:path";
            import { fileURLToPath } from "node:url";

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

            async function runArbiterHook(envelope: unknown, cwd: string): Promise<ArbiterDecision> {
              let proc: ReturnType<typeof Bun.spawn>;
              try {
                proc = Bun.spawn(["kyber-weave-arbiter", "hook", "--harness", {{literal}}], {
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

            export const KyberArbiter: Plugin = async ({ directory }) => ({
              "tool.execute.before": async (input, output) => {
                const root = resolveRepositoryRoot(directory);
                const envelope = {
                  schema: "kyber-arbiter.plugin-event/v1",
                  harness: {{literal}},
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
                  harness: {{literal}},
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

        return template + "\n" + defaultExport;
    }
}
