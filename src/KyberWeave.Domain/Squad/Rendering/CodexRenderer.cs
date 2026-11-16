using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
///     Renders canonical Squad source into Codex's native agent TOML and skill file formats.
/// </summary>
/// <remarks>
///     <para>
///         Native agent target: canonical agents render as Codex's native agent TOML primitive at
///         <c>.codex/agents/&lt;name&gt;.toml</c>. Codex agent TOML files contain the required
///         top-level fields <c>name</c>, <c>description</c>, and multi-line
///         <c>developer_instructions</c>, plus optional <c>model</c>.
///     </para>
///     <para>
///         Canonical skills render as harness skills at <c>.codex/skills/&lt;name&gt;/SKILL.md</c>
///         with YAML frontmatter containing <c>name</c>, <c>description</c>, and <c>license: MIT</c>.
///         Per the native single-projection rule, profile-declared shared identities suppress their
///         skill projections.
///     </para>
///     <para>
///         Model resolution resolves the agent model from <c>models.yml</c> for target <c>codex</c>,
///         falling back to the profile's default when not <c>inherit</c>.
///     </para>
///     <para>
///         Permission degradation: Codex agent configuration has no frontmatter tool allow-list or
///         capability permission lattice. Non-deny profile decisions are recorded as structured
///         degradations with code <c>permission-not-expressible</c> rather than inventing unenforceable fields.
///     </para>
///     <para>
///         <b>Arbiter block (Req 6.2, 8.1, 8.2, 22.2).</b> When the render request carries an
///         enabled <see cref="SquadArbiterWiring"/> under <see cref="SquadDeploymentScope.Project"/>,
///         the renderer returns a <c>codex</c> owned block for <c>.codex/hooks.json</c> holding
///         <c>PreToolUse</c> and <c>PostToolUse</c> matcher groups: the shared project-wide hook
///         <c>kyber-weave-arbiter hook --harness codex</c> (no <c>--caller</c>, since every
///         shared-file target carries project-wide hooks) matching <c>spawn_agent</c>, with the
///         wiring's timeout. No whole file is emitted; the deployment plan splices the block into
///         whatever the user already has. Under Global scope there is no project configuration to
///         enforce from, so nothing renders.
///     </para>
/// </remarks>
public sealed class CodexRenderer : SquadHarnessRenderer
{
    /// <summary>
    /// Matches Codex's subagent spawn tool under any harness namespace prefix, and the
    /// bare <c>Agent</c> name for the same tool invoked without qualification.
    /// </summary>
    private const string ArbiterMatcher = "^(Agent|(.*[._:/])?spawn_agent)$";

    private static readonly SquadHarnessSpec HarnessSpec = new(
        SquadTarget.Codex,
        "Codex",
        ".codex",
        "agents",
        SquadModelPolicy.HarnessValueVerbatim);

    public CodexRenderer()
        : base(HarnessSpec)
    {
    }

    /// <summary>Codex always carried a block list, empty when Arbiter is off.</summary>
    private protected override SquadRenderResult Render(SquadHarnessRenderContext context)
    {
        base.Render(context);
        return context.ToResult(emptyBlocks: true);
    }

    /// <summary>
    /// Every agent renders as a TOML agent file. A primary agent whose fallback profile declares
    /// <c>no-primary-agent: skill</c> also renders as an entry-point skill, unless the same-name
    /// canonical skill already is the entry point.
    /// </summary>
    private protected override void RenderAgent(SquadHarnessRenderContext context, SquadAgent agent)
    {
        context.AddPrincipal(RenderAgentFile(context, agent), agent.Resources);

        if (agent.Invocation == SquadInvocation.Primary &&
            context.ResolvePrimaryLowering(agent, () =>
                $"Cannot render primary agent '{agent.Name}' as a Codex entry-point skill: " +
                $"a canonical skill named '{agent.Name}' already occupies that identity, " +
                "and the identity is not declared as shared.") == SquadPrimaryLowering.EntryPointSkill)
        {
            context.AddPrincipal(
                context.RenderSkillDocument(agent.Name, agent.Description, agent.InstructionBody),
                agent.Resources);
        }

        context.AddDegradation(context.CapabilitiesNotExpressible(
            agent,
            context.NonDenyCapabilities(agent),
            "Codex agents cannot express capability permissions"));
    }

    /// <summary>Squad's owned <c>.codex/hooks.json</c> block.</summary>
    private protected override void RenderArbiter(SquadHarnessRenderContext context) =>
        context.AddArbiterMatcherGroupBlock(SquadHookBlockFormat.Codex, ArbiterMatcher);

    private static SquadDeploymentFile RenderAgentFile(SquadHarnessRenderContext context, SquadAgent agent)
    {
        StringBuilder builder = new();
        builder.Append("name = \"");
        builder.Append(EscapeTomlString(agent.Name));
        builder.Append("\"\ndescription = \"");
        builder.Append(EscapeTomlString(agent.Description));
        builder.Append("\"\n");

        string? model = context.ResolveModel(agent);
        if (model is not null)
        {
            builder.Append("model = \"");
            builder.Append(EscapeTomlString(model));
            builder.Append("\"\n");
        }

        string normalizedBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalizedBody.EndsWith('\n'))
        {
            normalizedBody += "\n";
        }

        builder.Append("developer_instructions = \"\"\"\n");
        builder.Append(EscapeTomlMultiline(normalizedBody));
        builder.Append("\"\"\"\n");

        return context.CreateFile($"{context.AgentsDirectory}/{agent.Name}.toml", builder.ToString());
    }

    private static string EscapeTomlString(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    /// <remarks>
    ///     Backslashes must be escaped before triple quotes: otherwise a body containing
    ///     <c>\"""</c> (or any backslash) produces invalid TOML when quotes are escaped first.
    /// </remarks>
    private static string EscapeTomlMultiline(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal);
    }
}
