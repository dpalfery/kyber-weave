namespace KyberWeave.Core.Configuration;

/// <summary>Root shape of a <c>kyber-weave.yml</c> file.</summary>
internal sealed class KyberWeaveYamlDocument
{
    public OntologyYamlSection? Ontology { get; set; }

    public HarnessYamlSection? Harness { get; set; }

    public DocsAnalysisYamlSection? DocsAnalysis { get; set; }

    public SquadYamlSection? Squad { get; set; }

    public ReviewYamlSection? Review { get; set; }

    public ArbiterYamlSection? Arbiter { get; set; }

    public McpYamlSection? Mcp { get; set; }

    /// <summary>
    /// Host additions to the configuration registry, as property name to repository-relative
    /// path. Bound as a plain map because the property names are the host's vocabulary, not
    /// a schema this type could enumerate.
    /// </summary>
    public Dictionary<string, string>? ConfigReg { get; set; }

    /// <summary>
    /// Raw <c>autonomy:</c> value. Kept a string so a value outside the two known settings
    /// reaches the loader as a configuration error rather than a deserialization failure.
    /// </summary>
    public string? Autonomy { get; set; }

    /// <summary>
    /// Raw <c>onboarding:</c> value — <c>done</c> or <c>declined</c> — or null when the
    /// repository never answered. Kept a string for the same reason as
    /// <see cref="Autonomy"/>: a value outside the two then reaches the loader as an error
    /// that names the setting and both words it accepts.
    /// </summary>
    public string? Onboarding { get; set; }

    /// <summary>The <c>run:</c> block: where a run starts and what it publishes.</summary>
    public RunYamlSection? Run { get; set; }

    /// <summary>
    /// Raw <c>stop-on-tool-failure:</c> value. Kept a string for the same reason as
    /// <see cref="Autonomy"/>: a value outside the two booleans then reaches the loader as an
    /// error that names the setting, instead of a type-conversion failure that names only a
    /// type an operator cannot act on.
    /// </summary>
    public string? StopOnToolFailure { get; set; }

    /// <summary>
    /// Raw <c>worktree-index-hook:</c> value. Kept a string for the same reason as
    /// <see cref="StopOnToolFailure"/>: a value outside the two booleans reaches the loader as an error that names
    /// the setting, not a type-conversion failure.
    /// </summary>
    public string? WorktreeIndexHook { get; set; }
}

/// <summary>
/// The <c>run:</c> block of a <c>kyber-weave.yml</c> file.
/// </summary>
/// <remarks>
/// Both members stay raw here. <c>harness-names</c> is kept a string so an unrecognised value
/// is refused by the loader that can name the setting, and <c>base-branch</c> is kept as
/// written because a branch name is a free string: its only default is applied where the
/// setting is read, not where it is parsed.
/// </remarks>
internal sealed class RunYamlSection
{
    /// <summary>The branch a run is compared against, or null when the operator named none.</summary>
    public string? BaseBranch { get; set; }

    /// <summary>Raw <c>harness-names:</c> value, kept a string for the reason on the type.</summary>
    public string? HarnessNames { get; set; }
}
