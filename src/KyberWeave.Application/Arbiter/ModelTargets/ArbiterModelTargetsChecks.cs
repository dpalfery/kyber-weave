namespace KyberWeave.Core.Arbiter.ModelTargets;

/// <summary>Completeness checks over an <see cref="ArbiterModelTargets"/> table.</summary>
public static class ArbiterModelTargetsChecks
{
    /// <summary>Gets whether this table is a complete v1 table written for <paramref name="harness"/>.</summary>
    /// <param name="targets">The table to check.</param>
    /// <param name="harness">The harness the caller asked for.</param>
    /// <returns>True when the schema, harness and every section match.</returns>
    public static bool IsCompleteFor(this ArbiterModelTargets targets, string harness) =>
        targets.Schema == ArbiterModelTargets.SchemaName
        && targets.Harness == harness
        && targets.Preset is not null
        && targets.BundleDigest is not null
        && targets.Profiles is not null
        && targets.Agents is not null;
}
