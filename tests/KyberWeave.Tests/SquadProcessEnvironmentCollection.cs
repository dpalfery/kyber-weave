using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Serializes the suites that mutate the process environment (<c>CODEX_HOME</c>,
/// <c>CLAUDE_CONFIG_DIR</c>) while driving real squad commands.
/// </summary>
/// <remarks>
/// The squad global-root resolver reads its per-target overrides through
/// <see cref="System.Environment.GetEnvironmentVariable(string)"/> at resolution time, so the
/// process environment is the seam those tests use to aim a real <c>--global</c> run at a
/// sandbox root. xUnit runs test classes in parallel by default, and two such classes writing
/// the same variable overlap: one class's <c>CODEX_HOME</c> lands under another's feet and the
/// command echoes a physical root the test never asked for. The defect is real but
/// scheduling-dependent, which is why it surfaced only once the suite got faster and the
/// classes began to overlap. Putting every process-environment mutator in one collection makes
/// them run one at a time, and <c>DisableParallelization</c> keeps any other class from
/// observing a variable mid-write. The suites are excluded from the fast tier, so the inner
/// loop pays nothing for the ordering.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit collection definitions are named <Name>Collection by convention; "
        + "the suffix is the framework idiom, not a claim to be a collection type.")]
public sealed class SquadProcessEnvironmentCollection
{
    public const string Name = "Squad process environment";
}
