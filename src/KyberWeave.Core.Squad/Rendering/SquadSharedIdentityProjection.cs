using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Resolves how a profile-declared shared identity projects onto each target.
/// </summary>
/// <remarks>
/// <para>
/// A shared identity is an agent and a canonical skill that share one name. There are two
/// shapes and they project differently, which is why this is resolved from the loaded source
/// rather than assumed from the shared-identities list alone.
/// </para>
/// <para>
/// A <b>subagent</b> shared identity collapses to a single projection on a native target: the
/// agent file is emitted and the canonical skill is suppressed. That is lossless only because
/// <see cref="Validation.SquadSourceValidator"/> requires the two bodies to be byte-identical
/// for a subagent identity, so the suppressed body carries nothing the agent file does not.
/// On a fallback target the canonical skill is reused instead of emitting a
/// <c>role-</c>-prefixed duplicate.
/// </para>
/// <para>
/// A <b>primary</b> shared identity is the thin-wrapper shape: the agent body is a pointer at
/// the same-name skill and the skill carries the role, so the bodies differ by design. The
/// canonical skill is therefore always emitted and <em>is</em> the entry point. Targets that
/// render a primary through an additional entry-point form (Claude, Factory) skip that
/// duplicate rather than failing closed; targets that lower a primary onto a skill or command
/// (Cursor, Pi, ZCode, Devin) reuse the canonical skill instead of emitting one of their own.
/// Suppressing the skill, as a subagent identity does, would leave the wrapper's pointer
/// dangling.
/// </para>
/// </remarks>
internal static class SquadSharedIdentityProjection
{
    /// <summary>The shared identities whose same-name agent is a primary-invocation wrapper.</summary>
    internal static HashSet<string> PrimaryIdentities(
        SquadSource source,
        IReadOnlySet<string> sharedIdentities) =>
        source.Agents
            .Where(agent => agent.Invocation == SquadInvocation.Primary &&
                            sharedIdentities.Contains(agent.Name))
            .Select(agent => agent.Name)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The shared identities whose canonical skill a renderer suppresses because the emitted
    /// agent file already carries the identical body. A primary shared identity is never
    /// suppressed: its skill is the entry point.
    /// </summary>
    internal static HashSet<string> SuppressedSkillIdentities(
        SquadSource source,
        IReadOnlySet<string> sharedIdentities) =>
        sharedIdentities
            .Except(PrimaryIdentities(source, sharedIdentities), StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="identity"/> is both declared as a shared identity and carried
    /// by a primary-invocation agent, i.e. the canonical skill is the entry point.
    /// </summary>
    internal static bool IsPrimarySharedIdentity(SquadSource source, string identity)
    {
        bool shared = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .Contains(identity, StringComparer.Ordinal);
        return shared && source.Agents.Any(agent =>
            string.Equals(agent.Name, identity, StringComparison.Ordinal) &&
            agent.Invocation == SquadInvocation.Primary);
    }
}
