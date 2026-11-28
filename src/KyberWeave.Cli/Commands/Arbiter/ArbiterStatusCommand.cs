using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Shows the effective provider, model, endpoint origin and key presence.</summary>
/// <remarks>
/// The key's value is never shown: the command prints only whether one resolved
/// for the endpoint origin. A remote provider with no resolving key raises
/// <c>KW-ARB-KEY-001</c> as a warning, matching <c>doctor</c>.
/// </remarks>
public sealed class ArbiterStatusCommand(
    IArbiterConfigResolver configs,
    IArbiterKeyService keys,
    IProcessEnvironment environment,
    IUserHostConfigStore userConfig,
    IArbiterRepositoryLocator locator,
    IArbiterHarnessGating gating,
    IAnsiConsole console) : AsyncCommand<ArbiterSettings>
{
    /// <summary>A remote provider is configured, and no key resolves for its origin.</summary>
    public const string KeyMissing = ArbiterKeyResolver.MissingKeyCode;

    /// <summary>
    /// Prints provider, model, endpoint origin and whether a key was found. Writes
    /// nothing. Exits 1 only when the configuration itself fails to load; a missing
    /// key is a warning and exits 0.
    /// </summary>
    public override async Task<int> ExecuteAsync(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DiagnosticReport report = new();

        StatusFacts? facts = null;
        if (configs.TryResolve(settings.Path, settings.Config, report, out KyberWeaveConfig config, out _))
        {
            // The key lookup and the repository-root resolution run processes, so they await here, before the fold
            // lifts the width; the fold then wraps the printing alone.
            facts = await CollectFactsAsync(config, settings.Path, cancellationToken).ConfigureAwait(false);
        }

        CommandHelpers.WriteUnfolded(console, () => Inspect(facts, report));

        CommandHelpers.Finish(console, report, settings, "arbiter status", "Rule");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>
    /// The facts the report prints: the provider with its resolved key, and the scope rows the key and the resolved
    /// root feed.
    /// </summary>
    /// <param name="Provider">The effective provider.</param>
    /// <param name="Origin">The endpoint origin, or null when the endpoint has none.</param>
    /// <param name="Remote">Whether the provider is remote, so a key is needed.</param>
    /// <param name="Key">The resolved key, or null when none resolves or the provider needs none.</param>
    /// <param name="GlobalTargets">The harnesses listed under <c>arbiter.global.targets</c>.</param>
    /// <param name="Gating">The per-harness gating rows for the project scope.</param>
    private sealed record StatusFacts(
        ArbiterProviderConfig Provider,
        string? Origin,
        bool Remote,
        string? Key,
        IReadOnlyList<string> GlobalTargets,
        HarnessGatingReport Gating);

    /// <summary>
    /// Resolves what the report prints: the key through the credential store and the repository root through git,
    /// each awaited rather than blocked, and the scope rows they feed.
    /// </summary>
    private async Task<StatusFacts> CollectFactsAsync(
        KyberWeaveConfig config,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        ArbiterProviderConfig provider = config.Arbiter.Provider;
        string? origin = ArbiterKeyResolver.GetOrigin(provider.Endpoint);
        bool remote = provider.Kind != ArbiterProviderKind.None
            && !ArbiterKeyResolver.IsLoopbackEndpoint(provider.Endpoint);
        string? key = remote ? await keys.TryResolveKeyAsync(config, provider.Endpoint, cancellationToken).ConfigureAwait(false) : null;
        string home = environment.HomeDirectory;
        IReadOnlyList<string> globalTargets = userConfig.ReadGlobalTargets(home);
        string root = await locator.ResolveRepositoryRootAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        HarnessGatingReport gatingReport = gating.Build(root, home, config, globalTargets);

        return new StatusFacts(provider, origin, remote, key, globalTargets, gatingReport);
    }

    private void Inspect(StatusFacts? facts, DiagnosticReport report)
    {
        if (facts is null)
        {
            return;
        }

        console.MarkupLine("[bold]Arbiter status[/]");
        console.MarkupLine($"  Provider: [bold]{Markup.Escape(KindName(facts.Provider.Kind))}[/]");
        console.MarkupLine($"  Model: [bold]{Markup.Escape(facts.Provider.Model)}[/]");
        console.MarkupLine($"  Endpoint: [bold]{Markup.Escape(facts.Provider.Endpoint)}[/]");
        console.MarkupLine($"  Origin: [bold]{Markup.Escape(facts.Origin ?? "none")}[/]");
        if (!facts.Remote)
        {
            console.MarkupLine("  Key: [grey]not needed (none or loopback provider).[/]");
        }
        else if (facts.Key is null)
        {
            console.MarkupLine("  Key: [yellow]missing.[/]");
            report.Add(new Diagnostic(
                KeyMissing,
                Severity.Warning,
                $"A remote provider is configured, and no key resolves for origin '{facts.Origin}'.",
                "arbiter.provider",
                Hint: $"Run 'arbiter setup' or export {ArbiterKeyResolver.EnvVarName}."));
        }
        else
        {
            console.MarkupLine("  Key: [green]found.[/]");
        }

        WriteGlobalScope(facts.GlobalTargets);
        WriteProjectScope(facts.Gating);
    }

    private void WriteGlobalScope(IReadOnlyList<string> targets)
    {
        console.MarkupLine("[bold]GLOBAL scope[/]");
        if (targets.Count == 0)
        {
            console.MarkupLine("  [grey]no global targets configured.[/]");
            return;
        }

        foreach (string token in targets)
        {
            string reason = GlobalReason(token);
            string label = TargetDisplayName(token);
            console.MarkupLine($"  {Markup.Escape(label)}: {Markup.Escape(reason)}");
        }
    }

    private void WriteProjectScope(HarnessGatingReport gating)
    {
        console.MarkupLine("[bold]PROJECT scope[/]");
        ArbiterGatingConsole.WriteRows(console, gating, TargetDisplayName);
    }

    private static string GlobalReason(string token)
    {
        if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
        {
            return "enforced-global";
        }

        IReadOnlyList<SquadDegradationRecord> records = ArbiterHookWiring.TargetDegradations(
            [target],
            SquadDeploymentScope.Global);
        return records.Count == 0 ? "enforced-global" : (records[0].Details ?? "enforced-global");
    }

    private static string TargetDisplayName(string token)
    {
        if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
        {
            return token;
        }

        return target switch
        {
            SquadTarget.Claude => "Claude",
            SquadTarget.Codex => "Codex",
            SquadTarget.Copilot => "Copilot",
            SquadTarget.Cursor => "Cursor",
            SquadTarget.OpenCode => "OpenCode",
            SquadTarget.Pi => "Pi",
            SquadTarget.Warp => "Warp",
            SquadTarget.ZCode => "ZCode",
            SquadTarget.Kilo => "Kilo",
            SquadTarget.Antigravity => "Antigravity",
            SquadTarget.Factory => "Factory",
            SquadTarget.Devin => "Devin",
            SquadTarget.CommandCode => "CommandCode",
            _ => token,
        };
    }

    private static string KindName(ArbiterProviderKind kind) =>
        kind == ArbiterProviderKind.Systemone ? "systemone" : "none";
}
