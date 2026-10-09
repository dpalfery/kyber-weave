namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders the TypeScript plugin shim that bridges a Bun-hosted harness to the
/// <c>kyber-weave-arbiter hook</c> binary through the task 4.3 plugin envelope.
/// </summary>
/// <remarks>
/// <para>
/// The generator is parameterized by harness token because Kilo takes the same shim
/// (task 16.2): only the <c>--harness</c> argv element and the envelope's
/// <c>harness</c> field vary. The spawned argv, the
/// <c>kyber-arbiter.plugin-event/v1</c> envelope shape
/// (<c>{schema, harness, phase, tool, call-id, session, cwd, args, result}</c>),
/// and the <c>{decision, reason, args}</c> reply contract stay identical.
/// </para>
/// <para>
/// The shim spawns by argv with <c>Bun.spawn</c> and no shell, writes the envelope
/// on stdin, ends stdin, and reads stdout as text. A <c>block</c> decision throws
/// <c>Error(reason)</c>; a returned <c>args</c> object is assigned to
/// <c>output.args</c> (before) and a block reason is appended to
/// <c>output.output</c> (after). A spawn failure throws, so the call is blocked
/// rather than passed through.
/// </para>
/// </remarks>
public static class ArbiterPluginShim
{
    /// <summary>Renders the plugin shim for the given harness token (for example <c>opencode</c>).</summary>
    public static string Render(string harnessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessToken);

        return $$"""
            import type { Plugin } from "@opencode-ai/plugin";

            type ArbiterDecision = {
              decision: "allow" | "block";
              reason?: string;
              args?: Record<string, unknown>;
            };

            async function runArbiterHook(envelope: unknown, cwd: string): Promise<ArbiterDecision> {
              let proc: ReturnType<typeof Bun.spawn>;
              try {
                proc = Bun.spawn(["kyber-weave-arbiter", "hook", "--harness", "{{harnessToken}}"], {
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
                return JSON.parse(text) as ArbiterDecision;
              } catch (err) {
                throw new Error("kyber-arbiter: failed to read arbiter decision: " + String(err));
              }
            }

            export const KyberArbiter: Plugin = async ({ directory }) => ({
              "tool.execute.before": async (input, output) => {
                const envelope = {
                  schema: "kyber-arbiter.plugin-event/v1",
                  harness: "{{harnessToken}}",
                  phase: "before",
                  tool: input.tool,
                  "call-id": input.callID,
                  session: input.sessionID,
                  cwd: directory,
                  args: input.args ?? output.args,
                };
                const decision = await runArbiterHook(envelope, directory);
                if (decision.decision === "block") {
                  throw new Error(decision.reason ?? "blocked by kyber-arbiter");
                }
                if (decision.args !== undefined) {
                  output.args = decision.args;
                }
              },
              "tool.execute.after": async (input, output) => {
                const envelope = {
                  schema: "kyber-arbiter.plugin-event/v1",
                  harness: "{{harnessToken}}",
                  phase: "after",
                  tool: input.tool,
                  "call-id": input.callID,
                  session: input.sessionID,
                  cwd: directory,
                  args: input.args,
                  result: {
                    title: output.title,
                    output: output.output,
                    metadata: output.metadata,
                  },
                };
                const decision = await runArbiterHook(envelope, directory);
                if (decision.decision === "block") {
                  const reason = decision.reason ?? "blocked by kyber-arbiter";
                  output.output = (output.output ?? "") + "\n" + reason;
                  throw new Error(reason);
                }
              },
            });
            """;
    }
}
